﻿//------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
//------------------------------------------------------------
namespace Microsoft.Azure.Cosmos.Linq
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Collections.Immutable;
    using System.Diagnostics;
    using System.Globalization;
    using System.Linq;
    using System.Linq.Expressions;
    using System.Reflection;
    using System.Runtime.Serialization;
    using Microsoft.Azure.Cosmos.CosmosElements;
    using Microsoft.Azure.Cosmos.CosmosElements.Numbers;
    using Microsoft.Azure.Cosmos.Spatial;
    using Microsoft.Azure.Cosmos.SqlObjects;
    using Microsoft.Azure.Documents;
    using Newtonsoft.Json;

    internal static class CosmosLinqSerializer
    {
        public static SqlScalarExpression ApplyCustomConverters(Expression left, SqlLiteralScalarExpression right)
        {
            MemberExpression memberExpression;
            if (left is UnaryExpression unaryExpression)
            {
                memberExpression = unaryExpression.Operand as MemberExpression;
            }
            else
            {
                memberExpression = left as MemberExpression;
            }

            if (memberExpression != null)
            {
                Type memberType = memberExpression.Type;
                if (memberType.IsNullable())
                {
                    memberType = memberType.NullableUnderlyingType();
                }

                // There are two ways to specify a custom attribute
                // 1- by specifying the JsonConverterAttribute on a Class/Enum
                //      [JsonConverter(typeof(StringEnumConverter))]
                //      Enum MyEnum
                //      {
                //           ...
                //      }
                //
                // 2- by specifying the JsonConverterAttribute on a property
                //      class MyClass
                //      {
                //           [JsonConverter(typeof(StringEnumConverter))]
                //           public MyEnum MyEnum;
                //      }
                //
                // Newtonsoft gives high precedence to the attribute specified
                // on a property over on a type (class/enum)
                // so we check both attributes and apply the same precedence rules
                // JsonConverterAttribute doesn't allow duplicates so it's safe to
                // use FirstOrDefault()
                CustomAttributeData memberAttribute = memberExpression.Member.CustomAttributes.Where(ca => ca.AttributeType == typeof(Newtonsoft.Json.JsonConverterAttribute)).FirstOrDefault();
                CustomAttributeData typeAttribute = memberType.GetsCustomAttributes().Where(ca => ca.AttributeType == typeof(Newtonsoft.Json.JsonConverterAttribute)).FirstOrDefault();

                CustomAttributeData converterAttribute = memberAttribute ?? typeAttribute;
                if (converterAttribute != null)
                {
                    Debug.Assert(converterAttribute.ConstructorArguments.Count > 0);

                    Type converterType = (Type)converterAttribute.ConstructorArguments[0].Value;

                    object value = default(object);
                    // Enum
                    if (memberType.IsEnum())
                    {
                        Number64 number64 = ((SqlNumberLiteral)right.Literal).Value;
                        if (number64.IsDouble)
                        {
                            value = Enum.ToObject(memberType, Number64.ToDouble(number64));
                        }
                        else
                        {
                            value = Enum.ToObject(memberType, Number64.ToLong(number64));
                        }

                    }
                    // DateTime
                    else if (memberType == typeof(DateTime))
                    {
                        SqlStringLiteral serializedDateTime = (SqlStringLiteral)right.Literal;
                        value = DateTime.Parse(serializedDateTime.Value, provider: null, DateTimeStyles.RoundtripKind);
                    }

                    if (value != default(object))
                    {
                        string serializedValue;

                        if (converterType.GetConstructor(Type.EmptyTypes) != null)
                        {
                            serializedValue = JsonConvert.SerializeObject(value, (Newtonsoft.Json.JsonConverter)Activator.CreateInstance(converterType));
                        }
                        else
                        {
                            serializedValue = JsonConvert.SerializeObject(value);
                        }

                        return CosmosElement.Parse(serializedValue).Accept(CosmosElementToSqlScalarExpressionVisitor.Singleton);
                    }
                }
            }

            return right;
        }

        public static SqlScalarExpression VisitConstant(ConstantExpression inputExpression, TranslationContext context)
        {
            if (inputExpression.Value == null)
            {
                return SqlLiteralScalarExpression.SqlNullLiteralScalarExpression;
            }

            if (inputExpression.Type.IsNullable())
            {
                return CosmosLinqSerializer.VisitConstant(Expression.Constant(inputExpression.Value, Nullable.GetUnderlyingType(inputExpression.Type)), context);
            }

            if (context.parameters != null && context.parameters.TryGetValue(inputExpression.Value, out string paramName))
            {
                SqlParameter sqlParameter = SqlParameter.Create(paramName);
                return SqlParameterRefScalarExpression.Create(sqlParameter);
            }

            Type constantType = inputExpression.Value.GetType();
            if (constantType.IsValueType())
            {
                if (inputExpression.Value is bool boolValue)
                {
                    SqlBooleanLiteral literal = SqlBooleanLiteral.Create(boolValue);
                    return SqlLiteralScalarExpression.Create(literal);
                }

                if (ExpressionToSql.TryGetSqlNumberLiteral(inputExpression.Value, out SqlNumberLiteral numberLiteral))
                {
                    return SqlLiteralScalarExpression.Create(numberLiteral);
                }

                if (inputExpression.Value is Guid guidValue)
                {
                    SqlStringLiteral literal = SqlStringLiteral.Create(guidValue.ToString());
                    return SqlLiteralScalarExpression.Create(literal);
                }
            }

            if (inputExpression.Value is string stringValue)
            {
                SqlStringLiteral literal = SqlStringLiteral.Create(stringValue);
                return SqlLiteralScalarExpression.Create(literal);
            }

            if (typeof(Geometry).IsAssignableFrom(constantType))
            {
                return GeometrySqlExpressionFactory.Construct(inputExpression);
            }

            if (inputExpression.Value is IEnumerable enumerable)
            {
                List<SqlScalarExpression> arrayItems = new List<SqlScalarExpression>();

                foreach (object item in enumerable)
                {
                    arrayItems.Add(CosmosLinqSerializer.VisitConstant(Expression.Constant(item), context));
                }

                return SqlArrayCreateScalarExpression.Create(arrayItems.ToImmutableArray());
            }

            return CosmosElement.Parse(JsonConvert.SerializeObject(inputExpression.Value)).Accept(CosmosElementToSqlScalarExpressionVisitor.Singleton);
        }

        public static string GetMemberName(this MemberInfo memberInfo, CosmosLinqSerializerOptions linqSerializerOptions = null)
        {
            string memberName = null;

            // Check if Newtonsoft JsonExtensionDataAttribute is present on the member, if so, return empty member name.
            Newtonsoft.Json.JsonExtensionDataAttribute jsonExtensionDataAttribute = memberInfo.GetCustomAttribute<Newtonsoft.Json.JsonExtensionDataAttribute>(true);
            if (jsonExtensionDataAttribute != null && jsonExtensionDataAttribute.ReadData)
            {
                return null;
            }

            // Json.Net honors JsonPropertyAttribute more than DataMemberAttribute
            // So we check for JsonPropertyAttribute first.
            JsonPropertyAttribute jsonPropertyAttribute = memberInfo.GetCustomAttribute<JsonPropertyAttribute>(true);
            if (jsonPropertyAttribute != null && !string.IsNullOrEmpty(jsonPropertyAttribute.PropertyName))
            {
                memberName = jsonPropertyAttribute.PropertyName;
            }
            else
            {
                DataContractAttribute dataContractAttribute = memberInfo.DeclaringType.GetCustomAttribute<DataContractAttribute>(true);
                if (dataContractAttribute != null)
                {
                    DataMemberAttribute dataMemberAttribute = memberInfo.GetCustomAttribute<DataMemberAttribute>(true);
                    if (dataMemberAttribute != null && !string.IsNullOrEmpty(dataMemberAttribute.Name))
                    {
                        memberName = dataMemberAttribute.Name;
                    }
                }
            }

            if (memberName == null)
            {
                memberName = memberInfo.Name;
            }

            if (linqSerializerOptions != null)
            {
                memberName = CosmosSerializationUtil.GetStringWithPropertyNamingPolicy(linqSerializerOptions, memberName);
            }

            return memberName;
        }

        private sealed class CosmosElementToSqlScalarExpressionVisitor : ICosmosElementVisitor<SqlScalarExpression>
        {
            public static readonly CosmosElementToSqlScalarExpressionVisitor Singleton = new CosmosElementToSqlScalarExpressionVisitor();

            private CosmosElementToSqlScalarExpressionVisitor()
            {
                // Private constructor, since this class is a singleton.
            }

            public SqlScalarExpression Visit(CosmosArray cosmosArray)
            {
                List<SqlScalarExpression> items = new List<SqlScalarExpression>();
                foreach (CosmosElement item in cosmosArray)
                {
                    items.Add(item.Accept(this));
                }

                return SqlArrayCreateScalarExpression.Create(items.ToImmutableArray());
            }

            public SqlScalarExpression Visit(CosmosBinary cosmosBinary)
            {
                // Can not convert binary to scalar expression without knowing the API type.
                throw new NotImplementedException();
            }

            public SqlScalarExpression Visit(CosmosBoolean cosmosBoolean)
            {
                return SqlLiteralScalarExpression.Create(SqlBooleanLiteral.Create(cosmosBoolean.Value));
            }

            public SqlScalarExpression Visit(CosmosGuid cosmosGuid)
            {
                // Can not convert guid to scalar expression without knowing the API type.
                throw new NotImplementedException();
            }

            public SqlScalarExpression Visit(CosmosNull cosmosNull)
            {
                return SqlLiteralScalarExpression.Create(SqlNullLiteral.Create());
            }

            public SqlScalarExpression Visit(CosmosNumber cosmosNumber)
            {
                if (!(cosmosNumber is CosmosNumber64 cosmosNumber64))
                {
                    throw new ArgumentException($"Unknown {nameof(CosmosNumber)} type: {cosmosNumber.GetType()}.");
                }

                return SqlLiteralScalarExpression.Create(SqlNumberLiteral.Create(cosmosNumber64.GetValue()));
            }

            public SqlScalarExpression Visit(CosmosObject cosmosObject)
            {
                List<SqlObjectProperty> properties = new List<SqlObjectProperty>();
                foreach (KeyValuePair<string, CosmosElement> prop in cosmosObject)
                {
                    SqlPropertyName name = SqlPropertyName.Create(prop.Key);
                    CosmosElement value = prop.Value;
                    SqlScalarExpression expression = value.Accept(this);
                    SqlObjectProperty property = SqlObjectProperty.Create(name, expression);
                    properties.Add(property);
                }

                return SqlObjectCreateScalarExpression.Create(properties.ToImmutableArray());
            }

            public SqlScalarExpression Visit(CosmosString cosmosString)
            {
                return SqlLiteralScalarExpression.Create(SqlStringLiteral.Create(cosmosString.Value));
            }

            public SqlScalarExpression Visit(CosmosUndefined cosmosUndefined)
            {
                return SqlLiteralScalarExpression.Create(SqlUndefinedLiteral.Create());
            }
        }
    }
}
