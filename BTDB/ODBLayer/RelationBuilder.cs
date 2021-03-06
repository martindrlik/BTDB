using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using BTDB.Buffer;
using BTDB.Collections;
using BTDB.FieldHandler;
using BTDB.IL;
using BTDB.KVDBLayer;
using BTDB.StreamLayer;

namespace BTDB.ODBLayer
{
    internal class RelationBuilder<T>
    {
        readonly RelationInfo _relationInfo;
        readonly Type _relationDbManipulatorType;

        public RelationBuilder(RelationInfo relationInfo, Type relationDbManipulatorType)
        {
            _relationInfo = relationInfo;
            _relationDbManipulatorType = relationDbManipulatorType;
        }

        public Func<IObjectDBTransaction, T> Build(string relationName)
        {
            var interfaceType = typeof(T);
            var classImpl = ILBuilder.Instance.NewType("Relation" + relationName, _relationDbManipulatorType,
                new[] {interfaceType});
            var constructorMethod =
                classImpl.DefineConstructor(new[] {typeof(IObjectDBTransaction), typeof(RelationInfo)});
            var il = constructorMethod.Generator;
            // super.ctor(transaction, relationInfo);
            il.Ldarg(0).Ldarg(1).Ldarg(2)
                .Call(_relationDbManipulatorType.GetConstructor(new[]
                    {typeof(IObjectDBTransaction), typeof(RelationInfo)}))
                .Ret();
            GenerateApartFieldsProperties(classImpl, interfaceType);
            var methods = RelationInfo.GetMethods(interfaceType);
            foreach (var method in methods)
            {
                if (method.Name.StartsWith("get_") || method.Name.StartsWith("set_"))
                    continue;
                var reqMethod = classImpl.DefineMethod("_R_" + method.Name, method.ReturnType,
                    method.GetParameters().Select(pi => pi.ParameterType).ToArray(),
                    MethodAttributes.Virtual | MethodAttributes.Public);
                if (method.Name.StartsWith("RemoveBy") || method.Name.StartsWith("ShallowRemoveBy"))
                {
                    var methodParameters = method.GetParameters();
                    if (method.Name == "RemoveByIdPartial")
                        BuildRemoveByIdPartialMethod(method, methodParameters, reqMethod);
                    else if (ParametersEndsWithAdvancedEnumeratorParam(methodParameters))
                        BuildRemoveByIdAdvancedParamMethod(method, methodParameters, reqMethod);
                    else
                        BuildRemoveByMethod(method, methodParameters, reqMethod);
                }
                else if (method.Name.StartsWith("FindBy"))
                {
                    BuildFindByMethod(method, reqMethod);
                }
                else if (method.Name == "Contains")
                {
                    BuildContainsMethod(method, reqMethod);
                }
                else if (method.Name == "CountById") //count by primary key
                {
                    BuildCountByIdMethod(method, reqMethod);
                }
                else if (method.Name == "AnyById") //any by primary key
                {
                    BuildAnyByIdMethod(method, reqMethod);
                }
                else if (method.Name.StartsWith("ListBy", StringComparison.Ordinal)
                ) //ListBy{Name}(tenantId, .., AdvancedEnumeratorParam)
                {
                    if (StripVariant(method.Name.Substring(6), false) == "Id")
                    {
                        // List by primary key
                        BuildListByIdMethod(method, reqMethod);
                    }
                    else
                    {
                        BuildListByMethod(method, reqMethod);
                    }
                }
                else if (method.Name.StartsWith("CountBy", StringComparison.Ordinal)
                ) //CountBy{Name}(tenantId, ..[, AdvancedEnumeratorParam])
                {
                    BuildCountByMethod(method, reqMethod);
                }
                else if (method.Name.StartsWith("AnyBy", StringComparison.Ordinal)
                ) //AnyBy{Name}(tenantId, ..[, AdvancedEnumeratorParam])
                {
                    BuildAnyByMethod(method, reqMethod);
                }
                else if (method.Name == "Insert")
                {
                    BuildInsertMethod(method, reqMethod);
                }
                else
                {
                    BuildManipulatorCallWithSameParameters(method, reqMethod);
                }

                reqMethod.Generator.Ret();
                classImpl.DefineMethodOverride(reqMethod, method);
            }

            var classImplType = classImpl.CreateType();

            return BuildRelationCreatorInstance<T>(classImplType, relationName, _relationInfo);
        }

        static bool ParametersEndsWithAdvancedEnumeratorParam(ParameterInfo[] methodParameters)
        {
            return methodParameters.Length > 0 && methodParameters[^1].ParameterType
                .InheritsOrImplements(typeof(AdvancedEnumeratorParam<>));
        }

        void BuildContainsMethod(MethodInfo method, IILMethod reqMethod)
        {
            var (writerLoc, pushWriter, ctxLocFactory) = WriterPushers(reqMethod.Generator);

            WriteShortPrefixIl(reqMethod.Generator, pushWriter, ObjectDB.AllRelationsPKPrefix);
            //ByteBufferWriter.WriteVUInt32(RelationInfo.Id);
            WriteIdIl(reqMethod.Generator, pushWriter, (int) _relationInfo.Id);
            var primaryKeyFields = _relationInfo.ClientRelationVersionInfo.GetPrimaryKeyFields();

            var count = SaveMethodParameters(reqMethod.Generator, "Contains", method.GetParameters(),
                _relationInfo.ApartFields, primaryKeyFields, writerLoc, ctxLocFactory);
            if (count != primaryKeyFields.Count)
                throw new BTDBException(
                    $"Number of parameters in Contains does not match primary key count {primaryKeyFields.Count}.");

            //call manipulator.Contains
            reqMethod.Generator
                .Ldarg(0); //manipulator
            //call byteBuffer.data
            var dataGetter = typeof(ByteBufferWriter).GetProperty("Data")!.GetGetMethod(true);
            reqMethod.Generator.Ldloc(writerLoc).Callvirt(dataGetter!);
            reqMethod.Generator.Callvirt(_relationDbManipulatorType.GetMethod("Contains")!);
        }

        void BuildFindByMethod(MethodInfo method, IILMethod reqMethod)
        {
            var (writerLoc, pushWriter, ctxLocFactory) = WriterPushers(reqMethod.Generator);

            var nameWithoutVariants = StripVariant(method.Name.Substring(6), true);
            if (nameWithoutVariants == "Id" || nameWithoutVariants == "IdOrDefault")
            {
                CreateMethodFindById(reqMethod.Generator, method.Name,
                    method.GetParameters(), method.ReturnType, _relationInfo.ApartFields, pushWriter, writerLoc,
                    ctxLocFactory);
            }
            else
            {
                CreateMethodFindBy(reqMethod.Generator, method.Name, method.GetParameters(),
                    method.ReturnType, _relationInfo.ApartFields, pushWriter, writerLoc, ctxLocFactory);
            }
        }

        void BuildRemoveByMethod(MethodInfo method, ParameterInfo[] methodParameters, IILMethod reqMethod)
        {
            var isPrefixBased = method.ReturnType == typeof(int); //returns number of removed items

            var (writerLoc, pushWriter, ctxLocFactory) = WriterPushers(reqMethod.Generator);

            if (isPrefixBased)
            {
                WriteShortPrefixIl(reqMethod.Generator, pushWriter, _relationInfo.Prefix);
            }
            else
            {
                WriteShortPrefixIl(reqMethod.Generator, pushWriter, ObjectDB.AllRelationsPKPrefix);
                //ByteBufferWriter.WriteVUInt32(RelationInfo.Id);
                WriteIdIl(reqMethod.Generator, pushWriter, (int) _relationInfo.Id);
            }

            var primaryKeyFields = _relationInfo.ClientRelationVersionInfo.GetPrimaryKeyFields();

            var count = SaveMethodParameters(reqMethod.Generator, method.Name, methodParameters,
                _relationInfo.ApartFields, primaryKeyFields, writerLoc, ctxLocFactory);
            if (!isPrefixBased && count != primaryKeyFields.Count)
                throw new BTDBException(
                    $"Number of parameters in {method.Name} does not match primary key count {primaryKeyFields.Count}.");

            //call manipulator.RemoveBy_
            reqMethod.Generator
                .Ldarg(0); //manipulator
            //call byteBuffer.data
            var dataGetter = typeof(ByteBufferWriter).GetProperty("Data")!.GetGetMethod(true);
            reqMethod.Generator.Ldloc(writerLoc).Callvirt(dataGetter!);
            if (isPrefixBased)
            {
                if (AllKeyPrefixesAreSame(_relationInfo.ClientRelationVersionInfo, count) &&
                    !_relationInfo.NeedImplementFreeContent())
                    reqMethod.Generator.Callvirt(
                        _relationDbManipulatorType.GetMethod("RemoveByKeyPrefixWithoutIterate")!);
                else
                    reqMethod.Generator.Callvirt(_relationDbManipulatorType.GetMethod("RemoveByPrimaryKeyPrefix")!);
            }
            else
            {
                reqMethod.Generator.LdcI4(ShouldThrowWhenKeyNotFound(method.Name, method.ReturnType) ? 1 : 0);
                reqMethod.Generator.Callvirt(_relationDbManipulatorType.GetMethod(method.Name)!);
                if (method.ReturnType == typeof(void))
                    reqMethod.Generator.Pop();
            }
        }

        void BuildRemoveByIdAdvancedParamMethod(MethodInfo method, ParameterInfo[] parameters, IILMethod reqMethod)
        {
            if (method.ReturnType != typeof(int))
                throw new BTDBException($"Return value in {method.Name} must be int.");

            var advEnumParamOrder = (ushort) parameters.Length;
            var advEnumParam = parameters[advEnumParamOrder - 1].ParameterType;
            var advEnumParamType = advEnumParam.GenericTypeArguments[0];

            var emptyBufferLoc = reqMethod.Generator.DeclareLocal(typeof(ByteBuffer));
            var prefixParamCount = parameters.Length - 1;

            var primaryKeyFields = _relationInfo.ClientRelationVersionInfo.GetPrimaryKeyFields();
            var field = primaryKeyFields.Skip(_relationInfo.ApartFields.Count + prefixParamCount).First();
            ValidateAdvancedEnumParameter(field, advEnumParamType, method.Name);

            reqMethod.Generator.Ldarg(0); //manipulator for call RemoveByIdAdvancedParam

            WritePrimaryKeyPrefixFinishedByAdvancedEnumerator(method, parameters, reqMethod, prefixParamCount,
                advEnumParamOrder, advEnumParam, field, emptyBufferLoc);
            reqMethod.Generator.Call(_relationDbManipulatorType.GetMethod("RemoveByIdAdvancedParam"));
        }

        void WritePrimaryKeyPrefixFinishedByAdvancedEnumerator(MethodInfo method,
            ReadOnlySpan<ParameterInfo> parameters,
            IILMethod reqMethod, int prefixParamCount, ushort advEnumParamOrder, Type advEnumParam,
            TableFieldInfo field,
            IILLocal emptyBufferLoc)
        {
            SavePKListPrefixBytes(reqMethod.Generator, method.Name,
                parameters[..^1], _relationInfo.ApartFields);
            reqMethod.Generator
                .LdcI4(prefixParamCount + _relationInfo.ApartFields.Count)
                .Ldarg(advEnumParamOrder).Ldfld(advEnumParam.GetField("Order"))
                .Ldarg(advEnumParamOrder).Ldfld(advEnumParam.GetField("StartProposition"));
            FillBufferWhenNotIgnoredKeyPropositionIl(advEnumParamOrder, field, emptyBufferLoc,
                advEnumParam.GetField("Start"), reqMethod.Generator);
            reqMethod.Generator
                .Ldarg(advEnumParamOrder).Ldfld(advEnumParam.GetField("EndProposition"));
            FillBufferWhenNotIgnoredKeyPropositionIl(advEnumParamOrder, field, emptyBufferLoc,
                advEnumParam.GetField("End"), reqMethod.Generator);
        }

        void WritePrimaryKeyPrefixFinishedByAdvancedEnumeratorWithoutOrder(MethodInfo method,
            ReadOnlySpan<ParameterInfo> parameters,
            IILMethod reqMethod, ushort advEnumParamOrder, Type advEnumParam, TableFieldInfo field,
            IILLocal emptyBufferLoc)
        {
            reqMethod.Generator.Ldarg(0);
            SavePKListPrefixBytes(reqMethod.Generator, method.Name,
                parameters[..^1], _relationInfo.ApartFields);
            reqMethod.Generator
                .Ldarg(advEnumParamOrder).Ldfld(advEnumParam.GetField("StartProposition"));
            FillBufferWhenNotIgnoredKeyPropositionIl(advEnumParamOrder, field, emptyBufferLoc,
                advEnumParam.GetField("Start"), reqMethod.Generator);
            reqMethod.Generator
                .Ldarg(advEnumParamOrder).Ldfld(advEnumParam.GetField("EndProposition"));
            FillBufferWhenNotIgnoredKeyPropositionIl(advEnumParamOrder, field, emptyBufferLoc,
                advEnumParam.GetField("End"), reqMethod.Generator);
        }

        void BuildRemoveByIdPartialMethod(MethodInfo method, ParameterInfo[] methodParameters, IILMethod reqMethod)
        {
            var isPrefixBased = method.ReturnType == typeof(int); //returns number of removed items

            if (!isPrefixBased || methodParameters.Length == 0 ||
                methodParameters[^1].ParameterType != typeof(int) ||
                methodParameters[^1].Name
                    .IndexOf("max", StringComparison.InvariantCultureIgnoreCase) == -1)
            {
                throw new BTDBException("Invalid shape of RemoveByIdPartial.");
            }

            var il = reqMethod.Generator;
            var (writerLoc, pushWriter, ctxLocFactory) = WriterPushers(il);

            WriteShortPrefixIl(il, pushWriter, _relationInfo.Prefix);

            var primaryKeyFields = _relationInfo.ClientRelationVersionInfo.GetPrimaryKeyFields();
            SaveMethodParameters(il, method.Name, methodParameters[..^1],
                _relationInfo.ApartFields, primaryKeyFields, writerLoc, ctxLocFactory);

            var dataGetter = typeof(ByteBufferWriter).GetProperty("Data")!.GetGetMethod(true);
            il
                .Ldarg(0) //manipulator
                .Ldloc(writerLoc).Callvirt(dataGetter!) //call byteBuffer.Data
                .Ldarg((ushort) methodParameters.Length)
                .Callvirt(_relationDbManipulatorType.GetMethod("RemoveByPrimaryKeyPrefixPartial")!);
        }

        static bool AllKeyPrefixesAreSame(RelationVersionInfo relationInfo, ushort count)
        {
            foreach (var sk in relationInfo.SecondaryKeys)
            {
                var skFields = sk.Value;
                var idx = 0;
                foreach (var field in skFields.Fields)
                {
                    if (!field.IsFromPrimaryKey)
                        return false;
                    if (field.Index != idx)
                        return false;
                    if (++idx == count)
                        break;
                }
            }

            return true;
        }

        void BuildListByIdMethod(MethodInfo method, IILMethod reqMethod)
        {
            var parameters = method.GetParameters();
            if (ParametersEndsWithAdvancedEnumeratorParam(parameters))
            {
                var advEnumParamOrder = (ushort) parameters.Length;
                var advEnumParam = parameters[advEnumParamOrder - 1].ParameterType;
                var advEnumParamType = advEnumParam.GenericTypeArguments[0];

                var emptyBufferLoc = reqMethod.Generator.DeclareLocal(typeof(ByteBuffer));
                var prefixParamCount = parameters.Length - 1;

                var primaryKeyFields = _relationInfo.ClientRelationVersionInfo.GetPrimaryKeyFields();
                var field = primaryKeyFields.Skip(_relationInfo.ApartFields.Count + prefixParamCount).First();
                ValidateAdvancedEnumParameter(field, advEnumParamType, method.Name);

                reqMethod.Generator.Ldarg(0).Castclass(typeof(IRelationDbManipulator));

                WritePrimaryKeyPrefixFinishedByAdvancedEnumerator(method, parameters, reqMethod, prefixParamCount,
                    advEnumParamOrder, advEnumParam, field, emptyBufferLoc);

                if (ReturnTypeIsEnumeratorOrEnumerable(method, out var itemType))
                {
                    //return new RelationAdvancedEnumerator<T>(relationManipulator,
                    //    prefixBytes, prefixFieldCount,
                    //    order,
                    //    startKeyProposition, startKeyBytes,
                    //    endKeyProposition, endKeyBytes, loaderIndex);
                    var enumType = typeof(RelationAdvancedEnumerator<>).MakeGenericType(itemType);
                    var advancedEnumeratorCtor =
                        enumType.GetConstructors().Single(ci => ci.GetParameters().Length == 9);
                    reqMethod.Generator
                        .LdcI4(_relationInfo.BuildIndexToItemLoaderInfos(itemType))
                        .Newobj(advancedEnumeratorCtor);
                }
                else if (ReturnTypeIsIOrderedDictionaryEnumerator(method, advEnumParamType, out itemType))
                {
                    reqMethod.Generator
                        .LdcI4(1); //init key reader

                    //return new RelationAdvancedOrderedEnumerator<T>(relationManipulator,
                    //    prefixBytes, prefixFieldCount,
                    //    order,
                    //    startKeyProposition, startKeyBytes,
                    //    endKeyProposition, endKeyBytes, initKeyReader, loaderIndex);
                    var enumType =
                        typeof(RelationAdvancedOrderedEnumerator<,>).MakeGenericType(advEnumParamType,
                            itemType);
                    var advancedEnumeratorCtor =
                        enumType.GetConstructors().Single(ci => ci.GetParameters().Length == 10);
                    reqMethod.Generator
                        .LdcI4(_relationInfo.BuildIndexToItemLoaderInfos(itemType))
                        .Newobj(advancedEnumeratorCtor);
                }
                else
                {
                    throw new BTDBException("Invalid method " + method.Name);
                }
            }
            else
            {
                reqMethod.Generator.Ldarg(0).Castclass(typeof(IRelationDbManipulator));
                SavePKListPrefixBytes(reqMethod.Generator, method.Name, parameters, _relationInfo.ApartFields);
                reqMethod.Generator.LdcI4(parameters.Length + _relationInfo.ApartFields.Count);

                if (ReturnTypeIsEnumeratorOrEnumerable(method, out var itemType))
                {
                    //return new RelationAdvancedEnumerator<T>(relationManipulator,
                    //    prefixBytes, prefixFieldCount, loaderIndex);
                    var enumType = typeof(RelationAdvancedEnumerator<>).MakeGenericType(itemType);
                    var advancedEnumeratorCtor =
                        enumType.GetConstructors().Single(ci => ci.GetParameters().Length == 4);
                    reqMethod.Generator
                        .LdcI4(_relationInfo.BuildIndexToItemLoaderInfos(itemType))
                        .Newobj(advancedEnumeratorCtor);
                }
                else
                {
                    throw new BTDBException("Invalid method " + method.Name);
                }
            }
        }

        static bool TypeIsEnumeratorOrEnumerable(Type type, [NotNullWhen(true)] out Type? itemType)
        {
            itemType = type.SpecializationOf(typeof(IEnumerator<>)) ??
                       type.SpecializationOf(typeof(IEnumerable<>));

            if (itemType != null)
            {
                itemType = itemType.GenericTypeArguments[0];
            }

            return itemType != null;
        }

        static bool ReturnTypeIsEnumeratorOrEnumerable(MethodInfo method, [NotNullWhen(true)] out Type? itemType)
        {
            return TypeIsEnumeratorOrEnumerable(method.ReturnType, out itemType);
        }

        static bool ReturnTypeIsIOrderedDictionaryEnumerator(MethodInfo method, Type advEnumParamType,
            [NotNullWhen(true)] out Type? itemType)
        {
            itemType = method.ReturnType.SpecializationOf(typeof(IOrderedDictionaryEnumerator<,>));
            if (itemType != null)
            {
                var ta = itemType.GenericTypeArguments;
                if (ta[0] != advEnumParamType)
                {
                    itemType = null;
                    return false;
                }

                itemType = ta[1];
                return true;
            }

            return false;
        }


        void BuildCountByIdMethod(MethodInfo method, IILMethod reqMethod)
        {
            var parameters = method.GetParameters();
            var resultConversion = CheckLongLikeResult(method);
            if (ParametersEndsWithAdvancedEnumeratorParam(parameters))
            {
                PrepareAnyCountByIdWithAep(method, reqMethod, parameters);

                //return relationManipulator.CountWithProposition(prefixBytes,
                //    startKeyProposition, startKeyBytes, endKeyProposition, endKeyBytes);
                var calcCountMethod = _relationDbManipulatorType.GetMethod("CountWithProposition");
                reqMethod.Generator.Call(calcCountMethod);
            }
            else
            {
                reqMethod.Generator.Ldarg(0);
                SavePKListPrefixBytes(reqMethod.Generator, method.Name, parameters, _relationInfo.ApartFields);

                //return relationManipulator.CountWithPrefix(prefixBytes);
                var calcCountMethod = _relationDbManipulatorType.GetMethod("CountWithPrefix");
                reqMethod.Generator.Call(calcCountMethod);
            }

            resultConversion(reqMethod.Generator);
        }

        void BuildAnyByIdMethod(MethodInfo method, IILMethod reqMethod)
        {
            var parameters = method.GetParameters();
            CheckReturnType(method.Name, typeof(bool), method.ReturnType);
            if (ParametersEndsWithAdvancedEnumeratorParam(parameters))
            {
                PrepareAnyCountByIdWithAep(method, reqMethod, parameters);

                //return relationManipulator.AnyWithProposition(prefixBytes,
                //    startKeyProposition, startKeyBytes, endKeyProposition, endKeyBytes);
                var calcCountMethod = _relationDbManipulatorType.GetMethod("AnyWithProposition");
                reqMethod.Generator.Call(calcCountMethod);
            }
            else
            {
                reqMethod.Generator.Ldarg(0);
                SavePKListPrefixBytes(reqMethod.Generator, method.Name, parameters, _relationInfo.ApartFields);

                //return relationManipulator.AnyWithPrefix(prefixBytes);
                var calcCountMethod = _relationDbManipulatorType.GetMethod("AnyWithPrefix");
                reqMethod.Generator.Call(calcCountMethod);
            }
        }

        void PrepareAnyCountByIdWithAep(MethodInfo method, IILMethod reqMethod, ParameterInfo[] parameters)
        {
            var advEnumParamOrder = (ushort) parameters.Length;
            var advEnumParam = parameters[advEnumParamOrder - 1].ParameterType;
            var advEnumParamType = advEnumParam.GenericTypeArguments[0];

            var emptyBufferLoc = reqMethod.Generator.DeclareLocal(typeof(ByteBuffer));
            var prefixParamCount = parameters.Length - 1;

            var primaryKeyFields = _relationInfo.ClientRelationVersionInfo.GetPrimaryKeyFields();
            var field = primaryKeyFields.Skip(_relationInfo.ApartFields.Count + prefixParamCount).First();
            ValidateAdvancedEnumParameter(field, advEnumParamType, method.Name);

            WritePrimaryKeyPrefixFinishedByAdvancedEnumeratorWithoutOrder(method, parameters, reqMethod,
                advEnumParamOrder, advEnumParam, field, emptyBufferLoc);
        }

        void BuildListByMethod(MethodInfo method, IILMethod reqMethod)
        {
            var parameters = method.GetParameters();
            if (ParametersEndsWithAdvancedEnumeratorParam(parameters))
            {
                var advEnumParamOrder = (ushort) parameters.Length;
                var advEnumParam = parameters[advEnumParamOrder - 1].ParameterType;
                var advEnumParamType = advEnumParam.GenericTypeArguments[0];

                var emptyBufferLoc = reqMethod.Generator.DeclareLocal(typeof(ByteBuffer));
                var secondaryKeyIndex =
                    _relationInfo.ClientRelationVersionInfo.GetSecondaryKeyIndex(
                        StripVariant(method.Name.Substring(6), false));
                var prefixParamCount = parameters.Length - 1;

                var skFields = _relationInfo.ClientRelationVersionInfo.GetSecondaryKeyFields(secondaryKeyIndex);
                var field = skFields.Skip(_relationInfo.ApartFields.Count + prefixParamCount).First();
                ValidateAdvancedEnumParameter(field, advEnumParamType, method.Name);

                reqMethod.Generator
                    .Ldarg(0).Castclass(typeof(IRelationDbManipulator));
                SaveListPrefixBytes(secondaryKeyIndex, reqMethod.Generator, method.Name,
                    parameters[..^1], _relationInfo.ApartFields);
                reqMethod.Generator
                    .LdcI4(prefixParamCount + _relationInfo.ApartFields.Count)
                    .Ldarg(advEnumParamOrder).Ldfld(advEnumParam.GetField("Order"))
                    .Ldarg(advEnumParamOrder).Ldfld(advEnumParam.GetField("StartProposition"));
                FillBufferWhenNotIgnoredKeyPropositionIl(advEnumParamOrder, field,
                    emptyBufferLoc, advEnumParam.GetField("Start"), reqMethod.Generator);
                reqMethod.Generator
                    .Ldarg(advEnumParamOrder).Ldfld(advEnumParam.GetField("EndProposition"));
                FillBufferWhenNotIgnoredKeyPropositionIl(advEnumParamOrder, field,
                    emptyBufferLoc, advEnumParam.GetField("End"), reqMethod.Generator);
                reqMethod.Generator
                    .LdcI4((int) secondaryKeyIndex);

                if (ReturnTypeIsEnumeratorOrEnumerable(method, out var itemType))
                {
                    //return new RelationAdvancedSecondaryKeyEnumerator<T>(relationManipulator,
                    //    prefixBytes, prefixFieldCount,
                    //    order,
                    //    startKeyProposition, startKeyBytes,
                    //    endKeyProposition, endKeyBytes, secondaryKeyIndex, loaderIndex);
                    var enumType =
                        typeof(RelationAdvancedSecondaryKeyEnumerator<>).MakeGenericType(itemType);
                    var advancedEnumeratorCtor =
                        enumType.GetConstructors().Single(ci => ci.GetParameters().Length == 10);
                    reqMethod.Generator
                        .LdcI4(_relationInfo.BuildIndexToItemLoaderInfos(itemType))
                        .Newobj(advancedEnumeratorCtor);
                }
                else if (ReturnTypeIsIOrderedDictionaryEnumerator(method, advEnumParamType, out itemType))
                {
                    //return new RelationAdvancedOrderedSecondaryKeyEnumerator<T>(relationManipulator,
                    //    prefixBytes, prefixFieldCount,
                    //    order,
                    //    startKeyProposition, startKeyBytes,
                    //    endKeyProposition, endKeyBytes, secondaryKeyIndex, loaderIndex);
                    var enumType =
                        typeof(RelationAdvancedOrderedSecondaryKeyEnumerator<,>).MakeGenericType(advEnumParamType,
                            itemType);
                    var advancedEnumeratorCtor = enumType.GetConstructors()[0];
                    reqMethod.Generator
                        .LdcI4(_relationInfo.BuildIndexToItemLoaderInfos(itemType))
                        .Newobj(advancedEnumeratorCtor);
                }
                else
                {
                    throw new BTDBException("Invalid method " + method.Name);
                }
            }
            else
            {
                var secondaryKeyIndex =
                    _relationInfo.ClientRelationVersionInfo.GetSecondaryKeyIndex(
                        StripVariant(method.Name.Substring(6), false));

                reqMethod.Generator
                    .Ldarg(0).Castclass(typeof(IRelationDbManipulator));
                SaveListPrefixBytes(secondaryKeyIndex, reqMethod.Generator, method.Name,
                    parameters, _relationInfo.ApartFields);
                reqMethod.Generator
                    .LdcI4(parameters.Length + _relationInfo.ApartFields.Count)
                    .LdcI4((int) secondaryKeyIndex);

                if (ReturnTypeIsEnumeratorOrEnumerable(method, out var itemType))
                {
                    //return new RelationAdvancedSecondaryKeyEnumerator<T>(relationManipulator,
                    //    prefixBytes, prefixFieldCount, secondaryKeyIndex, loaderIndex);
                    var enumType =
                        typeof(RelationAdvancedSecondaryKeyEnumerator<>).MakeGenericType(itemType);
                    var advancedEnumeratorCtor =
                        enumType.GetConstructors().Single(ci => ci.GetParameters().Length == 5);
                    reqMethod.Generator
                        .LdcI4(_relationInfo.BuildIndexToItemLoaderInfos(itemType))
                        .Newobj(advancedEnumeratorCtor);
                }
                else
                {
                    throw new BTDBException("Invalid method " + method.Name);
                }
            }
        }

        void BuildCountByMethod(MethodInfo method, IILMethod reqMethod)
        {
            var parameters = method.GetParameters();
            var secondaryKeyIndex =
                _relationInfo.ClientRelationVersionInfo.GetSecondaryKeyIndex(method.Name.Substring(7));
            var resultConversion = CheckLongLikeResult(method);

            if (ParametersEndsWithAdvancedEnumeratorParam(parameters))
            {
                PrepareAnyCountByWithAep(method, reqMethod, parameters, secondaryKeyIndex);

                //return relationManipulator.CountWithProposition(prefixBytes,
                //    startKeyProposition, startKeyBytes, endKeyProposition, endKeyBytes);
                var calcCountMethod = _relationDbManipulatorType.GetMethod("CountWithProposition");
                reqMethod.Generator.Call(calcCountMethod!);
            }
            else
            {
                reqMethod.Generator
                    .Ldarg(0);
                SaveListPrefixBytes(secondaryKeyIndex, reqMethod.Generator, method.Name,
                    parameters, _relationInfo.ApartFields);

                //return relationManipulator.CountWithPrefix(prefixBytes);
                var calcCountMethod = _relationDbManipulatorType.GetMethod("CountWithPrefix");
                reqMethod.Generator.Call(calcCountMethod!);
            }

            resultConversion(reqMethod.Generator);
        }

        void BuildAnyByMethod(MethodInfo method, IILMethod reqMethod)
        {
            var parameters = method.GetParameters();
            var secondaryKeyIndex =
                _relationInfo.ClientRelationVersionInfo.GetSecondaryKeyIndex(method.Name.Substring(5));
            CheckReturnType(method.Name, typeof(bool), method.ReturnType);

            if (ParametersEndsWithAdvancedEnumeratorParam(parameters))
            {
                PrepareAnyCountByWithAep(method, reqMethod, parameters, secondaryKeyIndex);

                //return relationManipulator.AnyWithProposition(prefixBytes,
                //    startKeyProposition, startKeyBytes, endKeyProposition, endKeyBytes);
                var calcAnyMethod = _relationDbManipulatorType.GetMethod("AnyWithProposition");
                reqMethod.Generator.Call(calcAnyMethod);
            }
            else
            {
                reqMethod.Generator
                    .Ldarg(0);
                SaveListPrefixBytes(secondaryKeyIndex, reqMethod.Generator, method.Name,
                    parameters, _relationInfo.ApartFields);

                //return relationManipulator.AnyWithPrefix(prefixBytes);
                var calcAnyMethod = _relationDbManipulatorType.GetMethod("AnyWithPrefix");
                reqMethod.Generator.Call(calcAnyMethod);
            }
        }

        void PrepareAnyCountByWithAep(MethodInfo method, IILMethod reqMethod, ParameterInfo[] parameters,
            uint secondaryKeyIndex)
        {
            var advEnumParamOrder = (ushort) parameters.Length;
            var advEnumParam = parameters[advEnumParamOrder - 1].ParameterType;
            var advEnumParamType = advEnumParam.GenericTypeArguments[0];

            var emptyBufferLoc = reqMethod.Generator.DeclareLocal(typeof(ByteBuffer));
            var prefixParamCount = parameters.Length - 1;

            var skFields = _relationInfo.ClientRelationVersionInfo.GetSecondaryKeyFields(secondaryKeyIndex);
            var field = skFields.Skip(_relationInfo.ApartFields.Count + prefixParamCount).First();
            ValidateAdvancedEnumParameter(field, advEnumParamType, method.Name);

            reqMethod.Generator
                .Ldarg(0);
            SaveListPrefixBytes(secondaryKeyIndex, reqMethod.Generator, method.Name,
                parameters[..^1], _relationInfo.ApartFields);
            reqMethod.Generator
                .Ldarg(advEnumParamOrder).Ldfld(advEnumParam.GetField("StartProposition"));
            FillBufferWhenNotIgnoredKeyPropositionIl(advEnumParamOrder, field,
                emptyBufferLoc, advEnumParam.GetField("Start"), reqMethod.Generator);
            reqMethod.Generator
                .Ldarg(advEnumParamOrder).Ldfld(advEnumParam.GetField("EndProposition"));
            FillBufferWhenNotIgnoredKeyPropositionIl(advEnumParamOrder, field,
                emptyBufferLoc, advEnumParam.GetField("End"), reqMethod.Generator);
        }

        static Action<IILGen> CheckLongLikeResult(MethodInfo method)
        {
            var resultConversion =
                DefaultTypeConvertorGenerator.Instance.GenerateConversion(typeof(long), method.ReturnType);
            if (resultConversion == null)
            {
                throw new BTDBException("Invalid return type in method " + method.Name);
            }

            return resultConversion;
        }

        static void ValidateAdvancedEnumParameter(TableFieldInfo field, Type advEnumParamType, string methodName)
        {
            if (!field.Handler!.IsCompatibleWith(advEnumParamType, FieldHandlerOptions.Orderable))
            {
                throw new BTDBException(
                    $"Parameter type mismatch in {methodName} (expected '{field.Handler.HandledType().ToSimpleName()}' but '{advEnumParamType.ToSimpleName()}' found).");
            }
        }

        void BuildInsertMethod(MethodInfo method, IILMethod reqMethod)
        {
            var methodInfo = _relationDbManipulatorType.GetMethod(method.Name);
            bool returningBoolVariant;
            var returnType = method.ReturnType;
            if (returnType == typeof(void))
                returningBoolVariant = false;
            else if (returnType == typeof(bool))
                returningBoolVariant = true;
            else
                throw new BTDBException("Method Insert should be defined with void or bool return type.");
            var methodParams = method.GetParameters();
            CheckParameterCount(method.Name, 1, methodParams.Length);
            CheckParameterType(method.Name, 0, methodInfo.GetParameters()[0].ParameterType,
                methodParams[0].ParameterType);
            reqMethod.Generator
                .Ldarg(0) //this
                .Ldarg(1)
                .Callvirt(methodInfo);
            if (!returningBoolVariant)
            {
                var returnedTrueLabel = reqMethod.Generator.DefineLabel("returnedTrueLabel");
                reqMethod.Generator
                    .Brtrue(returnedTrueLabel)
                    .Ldstr("Trying to insert duplicate key.")
                    .Newobj(() => new BTDBException(null))
                    .Throw()
                    .Mark(returnedTrueLabel);
            }
        }

        void BuildManipulatorCallWithSameParameters(MethodInfo method, IILMethod reqMethod)
        {
            var methodParams = method.GetParameters();
            var paramCount = methodParams.Length;
            var methodInfo = _relationDbManipulatorType.GetMethod(method.Name);
            if (methodInfo == null)
                throw new BTDBException($"Method {method} is not supported.");
            CheckReturnType(method.Name, methodInfo.ReturnType, method.ReturnType);
            var calledMethodParams = methodInfo.GetParameters();
            CheckParameterCount(method.Name, calledMethodParams.Length, methodParams.Length);
            for (var i = 0; i < methodParams.Length; i++)
            {
                CheckParameterType(method.Name, i, calledMethodParams[i].ParameterType, methodParams[i].ParameterType);
            }

            for (ushort i = 0; i <= paramCount; i++)
                reqMethod.Generator.Ldarg(i);
            reqMethod.Generator.Callvirt(methodInfo);
        }

        static void CheckParameterType(string name, int parIdx, Type expectedType, Type actualType)
        {
            if (expectedType != actualType)
                throw new BTDBException($"Method {name} expects {parIdx}th parameter of type {expectedType.Name}.");
        }

        static void CheckParameterCount(string name, int expectedParameterCount, int actualParameterCount)
        {
            if (expectedParameterCount != actualParameterCount)
                throw new BTDBException($"Method {name} expects {expectedParameterCount} parameters count.");
        }

        static void CheckReturnType(string name, Type expectedReturnType, Type returnType)
        {
            if (returnType != expectedReturnType)
                throw new BTDBException($"Method {name} should be defined with {expectedReturnType.Name} return type.");
        }

        static Func<IObjectDBTransaction, T1> BuildRelationCreatorInstance<T1>(Type classImplType, string relationName,
            RelationInfo relationInfo)
        {
            var methodBuilder = ILBuilder.Instance.NewMethod("RelationFactory" + relationName,
                typeof(Func<IObjectDBTransaction, T1>), typeof(RelationInfo));
            var ilGenerator = methodBuilder.Generator;
            ilGenerator
                .Ldarg(1)
                .Ldarg(0)
                .Newobj(classImplType.GetConstructor(new[] {typeof(IObjectDBTransaction), typeof(RelationInfo)}))
                .Castclass(typeof(T1))
                .Ret();
            return (Func<IObjectDBTransaction, T1>) methodBuilder.Create(relationInfo);
        }

        void CreateMethodFindById(IILGen ilGenerator, string methodName,
            ParameterInfo[] methodParameters, Type methodReturnType, IDictionary<string, MethodInfo> apartFields,
            Action<IILGen> pushWriter,
            IILLocal writerLoc,
            Func<IILLocal> ctxLocFactory)
        {
            var isPrefixBased = TypeIsEnumeratorOrEnumerable(methodReturnType, out var itemType);
            if (isPrefixBased)
            {
                WriteShortPrefixIl(ilGenerator, pushWriter, _relationInfo.Prefix);
            }
            else
            {
                WriteShortPrefixIl(ilGenerator, pushWriter, ObjectDB.AllRelationsPKPrefix);
                //ByteBufferWriter.WriteVUInt32(RelationInfo.Id);
                WriteIdIl(ilGenerator, pushWriter, (int) _relationInfo.Id);
            }

            var primaryKeyFields = _relationInfo.ClientRelationVersionInfo.GetPrimaryKeyFields();

            var count = SaveMethodParameters(ilGenerator, methodName, methodParameters,
                apartFields, primaryKeyFields, writerLoc, ctxLocFactory);
            if (!isPrefixBased && count != primaryKeyFields.Count)
                throw new BTDBException(
                    $"Number of parameters in {methodName} does not match primary key count {primaryKeyFields.Count}.");

            //call manipulator.FindBy_
            ilGenerator
                .Ldarg(0); //manipulator
            //call byteBuffer.data
            var dataGetter = typeof(ByteBufferWriter).GetProperty("Data")!.GetGetMethod(true);
            ilGenerator.Ldloc(writerLoc).Call(dataGetter!);
            if (isPrefixBased)
            {
                ilGenerator.LdcI4(_relationInfo.BuildIndexToItemLoaderInfos(itemType));
                ilGenerator.Callvirt(
                    _relationDbManipulatorType.GetMethod("FindByPrimaryKeyPrefix")!.MakeGenericMethod(itemType));
                ilGenerator.Castclass(methodReturnType);
            }
            else
            {
                itemType = methodReturnType == typeof(void) ? _relationInfo.ClientType : methodReturnType;
                ilGenerator.LdcI4(ShouldThrowWhenKeyNotFound(methodName, methodReturnType) ? 1 : 0);
                ilGenerator.LdcI4(_relationInfo.BuildIndexToItemLoaderInfos(itemType));
                ilGenerator.Callvirt(
                    _relationDbManipulatorType.GetMethod("FindByIdOrDefault")!.MakeGenericMethod(itemType));
                if (methodReturnType == typeof(void))
                    ilGenerator.Pop();
            }
        }

        void CreateMethodFindBy(IILGen ilGenerator, string methodName,
            ParameterInfo[] methodParameters, Type methodReturnType, IDictionary<string, MethodInfo> apartFields,
            Action<IILGen> pushWriter,
            IILLocal writerLoc,
            Func<IILLocal> ctxLocFactory)
        {
            var allowDefault = false;
            var skName = StripVariant(methodName.Substring(6), true);
            if (skName.EndsWith("OrDefault"))
            {
                skName = skName.Substring(0, skName.Length - 9);
                allowDefault = true;
            }

            WriteShortPrefixIl(ilGenerator, pushWriter, ObjectDB.AllRelationsSKPrefix);
            var skIndex = _relationInfo.ClientRelationVersionInfo.GetSecondaryKeyIndex(skName);
            //ByteBuffered.WriteVUInt32(RelationInfo.Id);
            WriteIdIl(ilGenerator, pushWriter, (int) _relationInfo.Id);
            //ByteBuffered.WriteVUInt32(skIndex);
            WriteIdIl(ilGenerator, pushWriter, (int) skIndex);

            var secondaryKeyFields = _relationInfo.ClientRelationVersionInfo.GetSecondaryKeyFields(skIndex);
            SaveMethodParameters(ilGenerator, methodName, methodParameters,
                apartFields, secondaryKeyFields, writerLoc, ctxLocFactory);

            //call public T FindBySecondaryKeyOrDefault<T>(uint secondaryKeyIndex, uint prefixParametersCount, ByteBuffer secKeyBytes, bool throwWhenNotFound, int loaderIndex)
            ilGenerator.Ldarg(0); //manipulator
            ilGenerator.LdcI4((int) skIndex);
            ilGenerator.LdcI4(methodParameters.Length + apartFields.Count);
            //call byteBuffer.data
            var dataGetter = typeof(ByteBufferWriter).GetProperty("Data")!.GetGetMethod(true);
            ilGenerator.Ldloc(writerLoc).Callvirt(dataGetter!);
            if (TypeIsEnumeratorOrEnumerable(methodReturnType, out var itemType))
            {
                ilGenerator.LdcI4(_relationInfo.BuildIndexToItemLoaderInfos(itemType));
                ilGenerator.Callvirt(
                    _relationDbManipulatorType.GetMethod("FindBySecondaryKey")!.MakeGenericMethod(itemType));
                ilGenerator.Castclass(methodReturnType);
            }
            else
            {
                ilGenerator.LdcI4(allowDefault ? 0 : 1); //? should throw
                ilGenerator.LdcI4(_relationInfo.BuildIndexToItemLoaderInfos(methodReturnType));
                ilGenerator.Callvirt(
                    _relationDbManipulatorType.GetMethod("FindBySecondaryKeyOrDefault")!.MakeGenericMethod(
                        methodReturnType));
            }
        }

        string StripVariant(string name, bool withOrDefault)
        {
            var result = "";

            void Check(string id)
            {
                if (!name.StartsWith(id)) return;
                if (withOrDefault)
                {
                    if (name.Substring(id.Length).StartsWith("OrDefault"))
                    {
                        if (result.Length < id.Length + 9)
                        {
                            result = id + "OrDefault";
                        }
                    }
                }

                if (result.Length < id.Length)
                {
                    result = id;
                }
            }

            Check("Id");
            foreach (var secondaryKeyName in _relationInfo.ClientRelationVersionInfo.SecondaryKeys.Values.Select(s =>
                s.Name))
            {
                Check(secondaryKeyName);
            }

            return result.Length == 0 ? name : result;
        }

        static ushort SaveMethodParameters(IILGen ilGenerator, string methodName,
            ReadOnlySpan<ParameterInfo> methodParameters,
            IDictionary<string, MethodInfo> apartFields,
            IEnumerable<TableFieldInfo> fields, IILLocal writerLoc, Func<IILLocal> ctxLocFactory)
        {
            ushort usedApartFieldsCount = 0;
            ushort idx = 0;
            foreach (var field in fields)
            {
                if (apartFields.TryGetValue(field.Name, out var fieldGetter))
                {
                    usedApartFieldsCount++;
                    SaveKeyFieldFromApartField(ilGenerator, field, fieldGetter, writerLoc, ctxLocFactory);
                    continue;
                }

                if (idx == methodParameters.Length)
                {
                    break;
                }

                var par = methodParameters[idx++];
                if (string.Compare(field.Name, par.Name!.ToLower(), StringComparison.OrdinalIgnoreCase) != 0)
                {
                    throw new BTDBException($"Parameter and key mismatch in {methodName}, {field.Name}!={par.Name}.");
                }

                if (!field.Handler!.IsCompatibleWith(par.ParameterType, FieldHandlerOptions.Orderable))
                {
                    throw new BTDBException(
                        $"Parameter type mismatch in {methodName} (expected '{field.Handler.HandledType().ToSimpleName()}' but '{par.ParameterType.ToSimpleName()}' found).");
                }

                SaveKeyFieldFromArgument(ilGenerator, field, idx, par.ParameterType, writerLoc, ctxLocFactory);
            }

            if (usedApartFieldsCount != apartFields.Count)
            {
                throw new BTDBException($"Apart fields must be part of prefix in {methodName}.");
            }

            return (ushort) (idx + usedApartFieldsCount);
        }

        bool ShouldThrowWhenKeyNotFound(string methodName, Type methodReturnType)
        {
            if (methodName.StartsWith("RemoveBy"))
                return methodReturnType == typeof(void);
            if (StripVariant(methodName.Substring(6), true) == "IdOrDefault")
                return false;
            return true;
        }

        internal static void FillBufferWhenNotIgnoredKeyPropositionIl(ushort advEnumParamOrder, TableFieldInfo field,
            IILLocal emptyBufferLoc,
            FieldInfo instField, IILGen ilGenerator)
        {
            //stack contains KeyProposition
            var ignoreLabel = ilGenerator.DefineLabel(instField + "_ignore");
            var doneLabel = ilGenerator.DefineLabel(instField + "_done");
            var writerLoc = ilGenerator.DeclareLocal(typeof(AbstractBufferedWriter));
            ilGenerator
                .Dup()
                .LdcI4((int) KeyProposition.Ignored)
                .BeqS(ignoreLabel)
                .Newobj(() => new ByteBufferWriter())
                .Stloc(writerLoc);
            field.Handler.SpecializeSaveForType(instField.FieldType).Save(ilGenerator,
                il => il.Ldloc(writerLoc),
                il => il.Ldarg(advEnumParamOrder).Ldfld(instField));
            var dataGetter = typeof(ByteBufferWriter).GetProperty("Data").GetGetMethod(true);
            ilGenerator.Ldloc(writerLoc).Castclass(typeof(ByteBufferWriter)).Callvirt(dataGetter);
            ilGenerator
                .Br(doneLabel)
                .Mark(ignoreLabel)
                .Ldloc(emptyBufferLoc)
                .Mark(doneLabel);
        }

        void SaveListPrefixBytes(uint secondaryKeyIndex, IILGen ilGenerator, string methodName,
            ReadOnlySpan<ParameterInfo> methodParameters,
            IDictionary<string, MethodInfo> apartFields)
        {
            var (writerLoc, pushWriter, ctxLocFactory) = WriterPushers(ilGenerator);

            WriteShortPrefixIl(ilGenerator, pushWriter, ObjectDB.AllRelationsSKPrefix);
            //ByteBuffered.WriteVUInt32(RelationInfo.Id);
            WriteIdIl(ilGenerator, pushWriter, (int) _relationInfo.Id);
            //ByteBuffered.WriteVUInt32(skIndex);
            WriteIdIl(ilGenerator, pushWriter, (int) secondaryKeyIndex);

            var secondaryKeyFields = _relationInfo.ClientRelationVersionInfo.GetSecondaryKeyFields(secondaryKeyIndex);
            SaveMethodParameters(ilGenerator, methodName, methodParameters, apartFields,
                secondaryKeyFields, writerLoc, ctxLocFactory);

            var dataGetter = typeof(ByteBufferWriter).GetProperty("Data")!.GetGetMethod(true);
            ilGenerator.Ldloc(writerLoc).Callvirt(dataGetter!);
        }

        static (IILLocal, Action<IILGen>, Func<IILLocal>) WriterPushers(IILGen ilGenerator)
        {
            var writerLoc = ilGenerator.DeclareLocal(typeof(ByteBufferWriter));
            ilGenerator
                .Newobj(() => new ByteBufferWriter())
                .Stloc(writerLoc);

            void PushWriter(IILGen il) => il.Ldloc(writerLoc);

            IILLocal? ctxLoc = null;

            IILLocal PushCtx()
            {
                if (ctxLoc == null)
                {
                    ctxLoc = ilGenerator.DeclareLocal(typeof(IDBWriterCtx));
                    ilGenerator
                        .Ldarg(0)
                        .Callvirt(() => ((IRelationDbManipulator) null).Transaction)
                        .Do(PushWriter)
                        .Newobj(() => new DBWriterCtx(null, null))
                        .Stloc(ctxLoc);
                }

                return ctxLoc;
            }

            return (writerLoc, PushWriter, PushCtx);
        }

        void SavePKListPrefixBytes(IILGen ilGenerator, string methodName, ReadOnlySpan<ParameterInfo> methodParameters,
            IDictionary<string, MethodInfo> apartFields)
        {
            var (writerLoc, pushWriter, ctxLocFactory) = WriterPushers(ilGenerator);
            WriteShortPrefixIl(ilGenerator, pushWriter, _relationInfo.Prefix);

            var keyFields = _relationInfo.ClientRelationVersionInfo.GetPrimaryKeyFields();
            SaveMethodParameters(ilGenerator, methodName, methodParameters, apartFields,
                keyFields, writerLoc, ctxLocFactory);

            var dataGetter = typeof(ByteBufferWriter).GetProperty("Data")!.GetGetMethod(true);
            ilGenerator.Ldloc(writerLoc).Callvirt(dataGetter!);
        }

        void GenerateApartFieldsProperties(IILDynamicType classImpl, Type interfaceType)
        {
            var apartFields = new Dictionary<string, IILField>();
            var initializedFields = new Dictionary<string, IILField>();
            var methods = RelationInfo.GetMethods(interfaceType);
            var properties = RelationInfo.GetProperties(interfaceType).ToArray();
            foreach (var method in methods)
            {
                var name = method.Name;
                if (!name.StartsWith("get_") && !name.StartsWith("set_"))
                    continue;

                IILField field;
                IILField initCheckField;
                var propName = RelationInfo.GetPersistentName(method.Name.Substring(4), properties);

                if (!_relationInfo.ApartFields.ContainsKey(propName))
                    throw new BTDBException($"Invalid property name {propName}.");

                if (!apartFields.TryGetValue(propName, out field))
                {
                    field = classImpl.DefineField("_" + propName, method.ReturnType, FieldAttributes.Private);
                    apartFields[propName] = field;
                    initCheckField = classImpl.DefineField("_initialized_" + propName, typeof(bool),
                        FieldAttributes.Private);
                    initializedFields[propName] = initCheckField;
                }
                else
                {
                    initCheckField = initializedFields[propName];
                }

                var reqMethod = classImpl.DefineMethod(method.Name, method.ReturnType,
                    method.GetParameters().Select(pi => pi.ParameterType).ToArray(),
                    MethodAttributes.Virtual | MethodAttributes.Public);
                if (name.StartsWith("set_"))
                {
                    reqMethod.Generator.Ldarg(0).Ldarg(1).Stfld(field)
                        .Ldarg(0).LdcI4(1).Stfld(initCheckField)
                        .Ret();
                }
                else
                {
                    var initializedLabel = reqMethod.Generator.DefineLabel("initialized");
                    reqMethod.Generator
                        .Ldarg(0).Ldfld(initCheckField)
                        .Brtrue(initializedLabel)
                        .Ldstr($"Cannot use uninitialized apart field {propName}")
                        .Newobj(() => new BTDBException(null))
                        .Throw()
                        .Mark(initializedLabel)
                        .Ldarg(0).Ldfld(field).Ret();
                }

                classImpl.DefineMethodOverride(reqMethod, method);
            }
        }

        static void SaveKeyFieldFromArgument(IILGen ilGenerator, TableFieldInfo field, ushort parameterId,
            Type parameterType, IILLocal writerLoc, Func<IILLocal> ctxLocFactory)
        {
            var specialized = field.Handler!.SpecializeSaveForType(parameterType);
            var locToUse = specialized.NeedsCtx() ? ctxLocFactory() : writerLoc;
            specialized
                .Save(ilGenerator,
                    il => il.Ldloc(locToUse),
                    il => il.Ldarg(parameterId));
        }

        static void SaveKeyFieldFromApartField(IILGen ilGenerator, TableFieldInfo field, MethodInfo fieldGetter,
            IILLocal writerLoc, Func<IILLocal> ctxLocFactory)
        {
            var specialized = field.Handler!.SpecializeSaveForType(fieldGetter.ReturnType);
            var locToUse = specialized.NeedsCtx() ? ctxLocFactory() : writerLoc;
            specialized.Save(ilGenerator,
                il => il.Ldloc(locToUse),
                il => il.Ldarg(0).Callvirt(fieldGetter));
        }

        static void WriteIdIl(IILGen ilGenerator, Action<IILGen> pushWriter, int id)
        {
            var bytes = new byte[PackUnpack.LengthVUInt((uint) id)];
            int o = 0;
            PackUnpack.PackVUInt(bytes, ref o, (uint) id);
            WriteShortPrefixIl(ilGenerator, pushWriter, bytes);
        }

        static void WriteShortPrefixIl(IILGen ilGenerator, Action<IILGen> pushWriter, byte[] prefix)
        {
            foreach (byte b in prefix)
                ilGenerator
                    .Do(pushWriter)
                    .LdcI4(b)
                    .Call(() => default(AbstractBufferedWriter).WriteUInt8(0));
        }
    }
}
