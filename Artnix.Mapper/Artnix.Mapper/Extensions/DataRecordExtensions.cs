﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Data;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Artnix.MapperFramework.Providers;

namespace Artnix.MapperFramework.Extensions
{
    public static class DataRecordExtensions
    {
        public static HashSet<string> GetUpperCaseColumnNames(this IDataRecord reader)
        {
            return GetColumnNames(reader, colName => colName.ToUpper());
        }

        public static HashSet<string> GetColumnNames(this IDataRecord reader, Func<string, string> predicate)
        {
            var items = new HashSet<string>();
            for (int i = 0; i < reader.FieldCount; i++)
            {
                string name = reader.GetName(i);
                items.Add(predicate(name));
            }
            return items;
        }

        internal static Expression<Func<IDataRecord, TModel>> Map<TModel>(this IDataRecord reader, CasheConfig casheConfig)
            where TModel : class, new()
        {
            return Map<TModel>(GetPropertyNames<TModel>(reader, casheConfig));
        }

        private static IReadOnlyDictionary<string, string> GetPropertyNames<TModel>(IDataRecord reader, CasheConfig casheConfig)
            where TModel : class, new()
        {
            var columnNames = GetUpperCaseColumnNames(reader);
            Dictionary<string, string> columnNamesDic = casheConfig.useStandardCodeStyleForMembers ?
                columnNames.ToDictionary(p => p.Replace("_", ""), p => p) :
                columnNames.ToDictionary(p => p, p => p);

            var properties = casheConfig.ignoreMembers.IsNullOrEmpty()
                ? typeof(TModel).GetProperties()
                : typeof(TModel).GetProperties().Where(pi => !casheConfig.ignoreMembers.Contains(pi.Name));

            var piDic = properties.Where(pi => columnNamesDic.ContainsKey(pi.Name.ToUpper())).ToDictionary(pi => pi.Name, pi => columnNamesDic[pi.Name.ToUpper()]);
            if (!casheConfig.bindings.IsNullOrEmpty())
            {
                foreach (var b in casheConfig.bindings)
                    piDic[b.Key] = b.Value;
            }

            return new ReadOnlyDictionary<string, string>(piDic);
        }

        private static Expression<Func<IDataRecord, TModel>> Map<TModel>(IReadOnlyDictionary<string, string> bindings)
            where TModel : class, new()
        {
            Type modelType = typeof(TModel);
            IEnumerable<PropertyInfo> properties = modelType.GetProperties(bindings);

            var parameter = Expression.Parameter(typeof(IDataRecord), "ireader");

            var mi = typeof(IDataRecord).GetProperties()
                .FirstOrDefault(p => p.GetIndexParameters().Any(p1 => p1.ParameterType == typeof(string)))
                ?.GetMethod;

            var memberBindings = new List<MemberBinding>();
            foreach (PropertyInfo member in properties)
            {
                string name = member.Name;
                if (!bindings.IsNullOrEmpty())
                {
                    if (bindings.ContainsKey(member.Name))
                        name = bindings[member.Name];
                }

                var indexatorExp = Expression.Call(parameter, mi, Expression.Constant(name, typeof(string)));

                Expression valueExp;
                if (member.PropertyType.IsPrimitive)
                {
                    MethodInfo asTypeMethodInfo = typeof(Check).GetMethods().Single(p => p.Name == $"As{member.PropertyType.Name}");
                    valueExp = Expression.Call(asTypeMethodInfo, indexatorExp);
                }
                else
                {
                    if (Check.Nullable(member.PropertyType))
                    {
                        MethodInfo asTypeMethodInfo = typeof(Check).GetMethods().Single(p => p.Name == $"AsNullable{Check.GetUnderlyingType(member.PropertyType).Name}");
                        valueExp = Expression.Call(asTypeMethodInfo, indexatorExp);
                    }
                    else
                    {
                        MethodInfo dBNullValueMethodInfo = typeof(Check).GetMethods().Single(p => p.Name == nameof(Check.DBNullValue));
                        var nullableExp = Expression.Call(dBNullValueMethodInfo, indexatorExp);
                        valueExp = Expression.Convert(nullableExp, member.PropertyType);
                    }
                }
                memberBindings.Add(Expression.Bind(member, valueExp));
            }

            NewExpression model = Expression.New(modelType);
            MemberInitExpression memberInitExpression = Expression.MemberInit(model, memberBindings);
            return Expression.Lambda<Func<IDataRecord, TModel>>(memberInitExpression, parameter);
        }

        private static IEnumerable<PropertyInfo> GetProperties(this Type modelType, IReadOnlyDictionary<string, string> bindings)
        {
            bool IsNotClass(PropertyInfo pi) => !(pi.PropertyType.IsClass && pi.PropertyType.Name != typeof(string).Name);
            return bindings.IsNullOrEmpty() ?
                modelType.GetProperties().Where(IsNotClass) :
                modelType.GetProperties().Where(pi => bindings.ContainsKey(pi.Name) && IsNotClass(pi));
        }
    }
}