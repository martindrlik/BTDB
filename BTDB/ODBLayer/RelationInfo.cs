using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using BTDB.Buffer;
using BTDB.Collections;
using BTDB.FieldHandler;
using BTDB.IL;
using BTDB.KVDBLayer;
using BTDB.StreamLayer;

namespace BTDB.ODBLayer
{
    public class RelationInfo
    {
        readonly uint _id;
        readonly string _name;
        readonly IRelationInfoResolver _relationInfoResolver;
        readonly Type _interfaceType;
        readonly Type _clientType;
        readonly object _defaultClientObject;
        readonly ITypeConvertorGenerator _typeConvertorGenerator;

        RelationVersionInfo?[] _relationVersions = Array.Empty<RelationVersionInfo?>();
        Action<IInternalObjectDBTransaction, AbstractBufferedWriter, object, object> _primaryKeysSaver;
        Action<IInternalObjectDBTransaction, AbstractBufferedWriter, object> _valueSaver;

        internal StructList<ItemLoaderInfo> ItemLoaderInfos = new StructList<ItemLoaderInfo>();

        internal int BuildIndexToItemLoaderInfos(Type itemType)
        {
            for (var i = 0; i < ItemLoaderInfos.Count; i++)
            {
                if (ItemLoaderInfos[i].ItemType == itemType)
                {
                    return i;
                }
            }
            ItemLoaderInfos.Add(new ItemLoaderInfo(this, itemType));
            return (int)ItemLoaderInfos.Count - 1;
        }

        public class ItemLoaderInfo
        {
            internal readonly RelationInfo Owner;
            internal readonly Type ItemType;

            public ItemLoaderInfo(RelationInfo owner, Type itemType)
            {
                Owner = owner;
                ItemType = itemType;
                _valueLoaders = new Action<IInternalObjectDBTransaction, AbstractBufferedReader, object>?[Owner._relationVersions.Length];
                _primaryKeysLoader = CreatePkLoader(itemType, Owner.ClientRelationVersionInfo.GetPrimaryKeyFields(),
                    $"RelationKeyLoader_{Owner.Name}_{itemType.ToSimpleName()}");
            }

            internal object CreateInstance(IInternalObjectDBTransaction tr, ByteBuffer keyBytes, ByteBuffer valueBytes)
            {
                var reader = new ByteBufferReader(keyBytes);
                var obj = _primaryKeysLoader(tr, reader);
                reader.Restart(valueBytes);
                var version = reader.ReadVUInt32();
                GetValueLoader(version)(tr, reader, obj);
                return obj;
            }

            readonly Func<IInternalObjectDBTransaction, AbstractBufferedReader, object> _primaryKeysLoader;
            readonly Action<IInternalObjectDBTransaction, AbstractBufferedReader, object>?[] _valueLoaders;

            Action<IInternalObjectDBTransaction, AbstractBufferedReader, object> GetValueLoader(uint version)
            {
                Action<IInternalObjectDBTransaction, AbstractBufferedReader, object>? res;
                do
                {
                    res = _valueLoaders[version];
                    if (res != null) return res;
                    res = CreateLoader(ItemType,
                        Owner._relationVersions[version]!.GetValueFields(), $"RelationValueLoader_{Owner.Name}_{version}_{ItemType.ToSimpleName()}");
                } while (Interlocked.CompareExchange(ref _valueLoaders[version], res, null) != null);

                return res;
            }

            Func<IInternalObjectDBTransaction, AbstractBufferedReader, object> CreatePkLoader(Type instanceType,
                IEnumerable<TableFieldInfo> fields, string loaderName)
            {
                var method =
                    ILBuilder.Instance.NewMethod<Func<IInternalObjectDBTransaction, AbstractBufferedReader, object>>(
                        loaderName);
                var ilGenerator = method.Generator;
                ilGenerator.DeclareLocal(instanceType);
                ilGenerator
                    .Newobj(instanceType.GetConstructor(Type.EmptyTypes)!)
                    .Stloc(0);

                var loadInstructions = new StructList<(IFieldHandler, Action<IILGen>?, MethodInfo?)>();
                var props = instanceType.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                foreach (var srcFieldInfo in fields)
                {
                    var fieldInfo = props.FirstOrDefault(p => GetPersistentName(p) == srcFieldInfo.Name);
                    if (fieldInfo != null)
                    {
                        var setterMethod = fieldInfo.GetSetMethod(true);
                        var fieldType = setterMethod!.GetParameters()[0].ParameterType;
                        var specializedSrcHandler =
                            srcFieldInfo.Handler!.SpecializeLoadForType(fieldType, null);
                        var willLoad = specializedSrcHandler.HandledType();
                        var converterGenerator =
                            Owner._relationInfoResolver.TypeConvertorGenerator.GenerateConversion(willLoad, fieldType);
                        if (converterGenerator != null)
                        {
                            loadInstructions.Add((specializedSrcHandler, converterGenerator, setterMethod));
                            continue;
                        }
                    }

                    loadInstructions.Add((srcFieldInfo.Handler!, null, null));
                }

                // Remove useless skips from end
                while (loadInstructions.Count > 0 && loadInstructions.Last.Item2 == null)
                {
                    loadInstructions.RemoveAt(^1);
                }

                var anyNeedsCtx = false;
                for (var i = 0; i < loadInstructions.Count; i++)
                {
                    if (!loadInstructions[i].Item1.NeedsCtx()) continue;
                    anyNeedsCtx = true;
                    break;
                }

                if (anyNeedsCtx)
                {
                    ilGenerator.DeclareLocal(typeof(IReaderCtx));
                    ilGenerator
                        .Ldarg(0)
                        .Ldarg(1)
                        .Newobj(() => new DBReaderCtx(null, null))
                        .Stloc(1);
                }

                for (var i = 0; i < loadInstructions.Count; i++)
                {
                    ref var loadInstruction = ref loadInstructions[i];
                    Action<IILGen> readerOrCtx;
                    if (loadInstruction.Item1.NeedsCtx())
                        readerOrCtx = il => il.Ldloc(1);
                    else
                        readerOrCtx = il => il.Ldarg(1);
                    if (loadInstruction.Item2 != null)
                    {
                        ilGenerator.Ldloc(0);
                        loadInstruction.Item1.Load(ilGenerator, readerOrCtx);
                        loadInstruction.Item2(ilGenerator);
                        ilGenerator.Call(loadInstruction.Item3!);
                        continue;
                    }

                    loadInstruction.Item1.Skip(ilGenerator, readerOrCtx);
                }

                ilGenerator.Ldloc(0).Ret();
                return method.Create();
            }

            Action<IInternalObjectDBTransaction, AbstractBufferedReader, object> CreateLoader(Type instanceType,
                IEnumerable<TableFieldInfo> fields, string loaderName)
            {
                var method =
                    ILBuilder.Instance.NewMethod<Action<IInternalObjectDBTransaction, AbstractBufferedReader, object>>(
                        loaderName);
                var ilGenerator = method.Generator;
                ilGenerator.DeclareLocal(instanceType);
                ilGenerator
                    .Ldarg(2)
                    .Castclass(instanceType)
                    .Stloc(0);

                var instanceTableFieldInfos = new StructList<TableFieldInfo>();
                var loadInstructions = new StructList<(IFieldHandler, Action<IILGen>?, MethodInfo?, bool Init)>();
                var props = instanceType.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var persistentNameToPropertyInfo = new RefDictionary<string, PropertyInfo>();
                foreach (var pi in props)
                {
                    if (pi.GetCustomAttributes(typeof(NotStoredAttribute), true).Length != 0) continue;
                    if (pi.GetIndexParameters().Length != 0) continue;
                    var tfi = TableFieldInfo.Build(Owner.Name, pi, Owner._relationInfoResolver.FieldHandlerFactory,
                        FieldHandlerOptions.None);
                    instanceTableFieldInfos.Add(tfi);
                    persistentNameToPropertyInfo.GetOrAddValueRef(tfi.Name) = pi;
                }

                foreach (var srcFieldInfo in fields)
                {
                    var fieldInfo = persistentNameToPropertyInfo.GetOrFakeValueRef(srcFieldInfo.Name);
                    if (fieldInfo != null)
                    {
                        var setterMethod = fieldInfo.GetSetMethod(true);
                        var fieldType = setterMethod!.GetParameters()[0].ParameterType;
                        var specializedSrcHandler =
                            srcFieldInfo.Handler!.SpecializeLoadForType(fieldType, null);
                        var willLoad = specializedSrcHandler.HandledType();
                        var converterGenerator =
                            Owner._relationInfoResolver.TypeConvertorGenerator.GenerateConversion(willLoad, fieldType);
                        if (converterGenerator != null)
                        {
                            for (var i = 0; i < instanceTableFieldInfos.Count; i++)
                            {
                                if (instanceTableFieldInfos[i].Name != srcFieldInfo.Name) continue;
                                instanceTableFieldInfos.RemoveAt(i);
                                break;
                            }
                            loadInstructions.Add((specializedSrcHandler, converterGenerator, setterMethod, false));
                            continue;
                        }
                    }

                    loadInstructions.Add((srcFieldInfo.Handler!, null, null, false));
                }

                // Remove useless skips from end
                while (loadInstructions.Count > 0 && loadInstructions.Last.Item2 == null)
                {
                    loadInstructions.RemoveAt(^1);
                }

                foreach (var srcFieldInfo in instanceTableFieldInfos)
                {
                    var iFieldHandlerWithInit = srcFieldInfo.Handler as IFieldHandlerWithInit;
                    if (iFieldHandlerWithInit == null) continue;
                    var specializedSrcHandler = srcFieldInfo.Handler;
                    var willLoad = specializedSrcHandler.HandledType();
                    var fieldInfo = persistentNameToPropertyInfo.GetOrFakeValueRef(srcFieldInfo.Name);
                    var setterMethod = fieldInfo.GetSetMethod(true);
                    var converterGenerator =
                        Owner._relationInfoResolver.TypeConvertorGenerator.GenerateConversion(willLoad,
                            setterMethod!.GetParameters()[0].ParameterType);
                    if (converterGenerator == null) continue;
                    if (!iFieldHandlerWithInit.NeedInit()) continue;
                    loadInstructions.Add((specializedSrcHandler, converterGenerator, setterMethod, true));
                }

                var anyNeedsCtx = false;
                for (var i = 0; i < loadInstructions.Count; i++)
                {
                    if (!loadInstructions[i].Item1.NeedsCtx()) continue;
                    anyNeedsCtx = true;
                    break;
                }

                if (anyNeedsCtx)
                {
                    ilGenerator.DeclareLocal(typeof(IReaderCtx));
                    ilGenerator
                        .Ldarg(0)
                        .Ldarg(1)
                        .Newobj(() => new DBReaderCtx(null, null))
                        .Stloc(1);
                }

                for (var i = 0; i < loadInstructions.Count; i++)
                {
                    ref var loadInstruction = ref loadInstructions[i];
                    Action<IILGen> readerOrCtx;
                    if (loadInstruction.Item1.NeedsCtx())
                        readerOrCtx = il => il.Ldloc(1);
                    else
                        readerOrCtx = il => il.Ldarg(1);
                    if (loadInstruction.Item2 != null)
                    {
                        ilGenerator.Ldloc(0);
                        if (loadInstruction.Init)
                        {
                            ((IFieldHandlerWithInit)loadInstruction.Item1).Init(ilGenerator, readerOrCtx);
                        }
                        else
                        {
                            loadInstruction.Item1.Load(ilGenerator, readerOrCtx);
                        }
                        loadInstruction.Item2(ilGenerator);
                        ilGenerator.Call(loadInstruction.Item3!);
                        continue;
                    }

                    loadInstruction.Item1.Skip(ilGenerator, readerOrCtx);
                }

                ilGenerator.Ret();
                return method.Create();
            }
        }

        Action<IInternalObjectDBTransaction, AbstractBufferedReader, IList<ulong>>?[]
            _valueIDictFinders =
                Array.Empty<Action<IInternalObjectDBTransaction, AbstractBufferedReader, IList<ulong>>?>();

        //SK
        Action<IInternalObjectDBTransaction, AbstractBufferedWriter, object, object>
            [] //secondary key idx => sk key saver
            _secondaryKeysSavers;

        readonly ConcurrentDictionary<ulong, Action<IInternalObjectDBTransaction, AbstractBufferedWriter,
                AbstractBufferedReader, AbstractBufferedReader, object>>
            _secondaryKeysConvertSavers =
                new ConcurrentDictionary<ulong, Action<IInternalObjectDBTransaction, AbstractBufferedWriter,
                    AbstractBufferedReader, AbstractBufferedReader, object>>();

        readonly ConcurrentDictionary<ulong,
                Action<AbstractBufferedReader, AbstractBufferedReader, AbstractBufferedWriter>>
            _secondaryKeyValuetoPKLoader =
                new ConcurrentDictionary<ulong,
                    Action<AbstractBufferedReader, AbstractBufferedReader, AbstractBufferedWriter>>();

        public readonly struct SimpleLoaderType : IEquatable<SimpleLoaderType>
        {
            public IFieldHandler FieldHandler { get; }

            public Type RealType { get; }

            public SimpleLoaderType(IFieldHandler fieldHandler, Type realType)
            {
                FieldHandler = fieldHandler;
                RealType = realType;
            }

            public bool Equals(SimpleLoaderType other)
            {
                return FieldHandler == other.FieldHandler && RealType == other.RealType;
            }
        }

        readonly
            ConcurrentDictionary<SimpleLoaderType, object> //object is of type Action<AbstractBufferedReader, IReaderCtx, (object or value type same as in conc. dic. key)>
            _simpleLoader = new ConcurrentDictionary<SimpleLoaderType, object>();

        internal List<ulong> FreeContentOldDict { get; } = new List<ulong>();
        internal List<ulong> FreeContentNewDict { get; } = new List<ulong>();
        internal byte[] Prefix { get; private set; }

        bool? _needImplementFreeContent;

        public RelationInfo(uint id, string name, IRelationInfoResolver relationInfoResolver, Type interfaceType,
            IInternalObjectDBTransaction tr)
        {
            _id = id;
            _name = name;
            _relationInfoResolver = relationInfoResolver;
            _interfaceType = interfaceType;
            var methods = GetMethods(interfaceType).ToArray();
            _clientType = FindClientType(interfaceType.Name, methods);
            _defaultClientObject = Activator.CreateInstance(_clientType);

            CalculatePrefix();
            LoadUnresolvedVersionInfos(tr.KeyValueDBTransaction);
            ClientRelationVersionInfo = CreateVersionInfoByReflection();
            ResolveVersionInfos();
            ApartFields = FindApartFields(methods, interfaceType.GetProperties(), ClientRelationVersionInfo);
            if (LastPersistedVersion > 0 &&
                RelationVersionInfo.Equal(_relationVersions[LastPersistedVersion], ClientRelationVersionInfo))
            {
                _relationVersions[LastPersistedVersion] = ClientRelationVersionInfo;
                ClientTypeVersion = LastPersistedVersion;
                CreateCreatorLoadersAndSavers();
                CheckSecondaryKeys(tr, ClientRelationVersionInfo);
            }
            else
            {
                ClientTypeVersion = LastPersistedVersion + 1;
                _relationVersions[ClientTypeVersion] = ClientRelationVersionInfo;
                var writerKey = new ByteBufferWriter();
                writerKey.WriteByteArrayRaw(ObjectDB.RelationVersionsPrefix);
                writerKey.WriteVUInt32(_id);
                writerKey.WriteVUInt32(ClientTypeVersion);
                var writerValue = new ByteBufferWriter();
                ClientRelationVersionInfo.Save(writerValue);
                tr.KeyValueDBTransaction.SetKeyPrefix(ByteBuffer.NewEmpty());
                tr.KeyValueDBTransaction.CreateOrUpdateKeyValue(writerKey.Data, writerValue.Data);

                CreateCreatorLoadersAndSavers();
                if (LastPersistedVersion > 0)
                {
                    CheckThatPrimaryKeyHasNotChanged(tr, name, ClientRelationVersionInfo,
                        _relationVersions[LastPersistedVersion]);
                    UpdateSecondaryKeys(tr, ClientRelationVersionInfo, _relationVersions[LastPersistedVersion]);
                }
            }

            _typeConvertorGenerator = tr.Owner.TypeConvertorGenerator;
        }

        static Type FindClientType(string name, MethodInfo[] methods)
        {
            foreach (var method in methods)
            {
                if (method.Name != "Insert" && method.Name != "Update" && method.Name != "Upsert"
                    && method.Name != "ShallowUpdate" && method.Name != "ShallowUpsert")
                    continue;
                var @params = method.GetParameters();
                if (@params.Length != 1)
                    continue;
                return @params[0].ParameterType;
            }

            throw new BTDBException($"Cannot deduce client type from interface {name}");
        }

        void CheckThatPrimaryKeyHasNotChanged(IInternalObjectDBTransaction tr, string name,
            RelationVersionInfo info, RelationVersionInfo previousInfo)
        {
            var pkFields = info.GetPrimaryKeyFields();
            var prevPkFields = previousInfo.GetPrimaryKeyFields();
            if (pkFields.Count != prevPkFields.Count)
                throw new BTDBException(
                    $"Change of primary key in relation '{name}' is not allowed. Field count {pkFields.Count} != {prevPkFields.Count}.");
            var en = pkFields.GetEnumerator();
            var pen = prevPkFields.GetEnumerator();
            while (en.MoveNext() && pen.MoveNext())
            {
                if (ArePrimaryKeyFieldsCompatible(en.Current.Handler, pen.Current.Handler)) continue;
                var db = tr.Owner;
                db.Logger?.ReportIncompatiblePrimaryKey(name, en.Current.Name);
                if (db.ActualOptions.SelfHealing)
                {
                    ClearRelationData(tr, previousInfo);
                }
                else
                {
                    throw new BTDBException(
                        $"Change of primary key in relation '{name}' is not allowed. Field '{en.Current.Name}' is not compatible.");
                }
            }
        }

        static bool ArePrimaryKeyFieldsCompatible(IFieldHandler newHandler, IFieldHandler previousHandler)
        {
            var newHandledType = newHandler.HandledType();
            var previousHandledType = previousHandler.HandledType();
            if (newHandledType == previousHandledType)
                return true;
            if (newHandledType.IsEnum && previousHandledType.IsEnum)
            {
                var prevEnumCfg =
                    new EnumFieldHandler.EnumConfiguration((previousHandler as EnumFieldHandler).Configuration);
                var newEnumCfg = new EnumFieldHandler.EnumConfiguration((newHandler as EnumFieldHandler).Configuration);

                return prevEnumCfg.IsBinaryRepresentationSubsetOf(newEnumCfg);
            }

            return false;
        }

        public bool NeedImplementFreeContent()
        {
            if (!_needImplementFreeContent.HasValue)
            {
                _needImplementFreeContent = CalcNeedImplementFreeContent();
            }

            return _needImplementFreeContent.Value;
        }

        bool CalcNeedImplementFreeContent()
        {
            for (var i = 0; i < _relationVersions.Length; i++)
            {
                if (_relationVersions[i] == null) continue;
                var finder = GetIDictFinder((uint) i);
                if (finder != null)
                    return true;
            }

            return false;
        }

        void CheckSecondaryKeys(IInternalObjectDBTransaction tr, RelationVersionInfo info)
        {
            var count = GetRelationCount(tr);
            List<KeyValuePair<uint, SecondaryKeyInfo>> secKeysToAdd = null;
            foreach (var sk in info.SecondaryKeys)
            {
                if (WrongCountInSecondaryKey(tr.KeyValueDBTransaction, count, sk.Key))
                {
                    DeleteSecondaryKey(tr.KeyValueDBTransaction, sk.Key);
                    if (secKeysToAdd == null)
                        secKeysToAdd = new List<KeyValuePair<uint, SecondaryKeyInfo>>();
                    secKeysToAdd.Add(sk);
                }
            }

            if (secKeysToAdd?.Count > 0)
                CalculateSecondaryKey(tr, secKeysToAdd);
        }

        long GetRelationCount(IInternalObjectDBTransaction tr)
        {
            tr.KeyValueDBTransaction.SetKeyPrefix(Prefix);
            return tr.KeyValueDBTransaction.GetKeyValueCount();
        }

        void UpdateSecondaryKeys(IInternalObjectDBTransaction tr, RelationVersionInfo info,
            RelationVersionInfo previousInfo)
        {
            var count = GetRelationCount(tr);
            foreach (var prevIdx in previousInfo.SecondaryKeys.Keys)
            {
                if (!info.SecondaryKeys.ContainsKey(prevIdx))
                    DeleteSecondaryKey(tr.KeyValueDBTransaction, prevIdx);
            }

            var secKeysToAdd = new List<KeyValuePair<uint, SecondaryKeyInfo>>();
            foreach (var sk in info.SecondaryKeys)
            {
                if (!previousInfo.SecondaryKeys.ContainsKey(sk.Key))
                {
                    secKeysToAdd.Add(sk);
                }
                else if (WrongCountInSecondaryKey(tr.KeyValueDBTransaction, count, sk.Key))
                {
                    DeleteSecondaryKey(tr.KeyValueDBTransaction, sk.Key);
                    secKeysToAdd.Add(sk);
                }
            }

            if (secKeysToAdd.Count > 0)
                CalculateSecondaryKey(tr, secKeysToAdd);
        }

        bool WrongCountInSecondaryKey(IKeyValueDBTransaction tr, long count, uint index)
        {
            SetPrefixToSecondaryKey(tr, index);
            return count != tr.GetKeyValueCount();
        }

        void ClearRelationData(IInternalObjectDBTransaction tr, RelationVersionInfo info)
        {
            foreach (var prevIdx in info.SecondaryKeys.Keys)
            {
                DeleteSecondaryKey(tr.KeyValueDBTransaction, prevIdx);
            }

            var writer = new ByteBufferWriter();
            writer.WriteBlock(ObjectDB.AllRelationsPKPrefix);
            writer.WriteVUInt32(Id);

            tr.KeyValueDBTransaction.SetKeyPrefix(writer.Data);
            tr.KeyValueDBTransaction.EraseAll();
        }

        void DeleteSecondaryKey(IKeyValueDBTransaction keyValueTr, uint index)
        {
            SetPrefixToSecondaryKey(keyValueTr, index);
            keyValueTr.EraseAll();
        }

        void SetPrefixToSecondaryKey(IKeyValueDBTransaction keyValueTr, uint index)
        {
            var writer = new ByteBufferWriter();
            writer.WriteBlock(ObjectDB.AllRelationsSKPrefix);
            writer.WriteVUInt32(Id);
            writer.WriteVUInt32(index);

            keyValueTr.SetKeyPrefix(writer.Data);
        }

        void CalculateSecondaryKey(IInternalObjectDBTransaction tr, IList<KeyValuePair<uint, SecondaryKeyInfo>> indexes)
        {
            var keyWriter = new ByteBufferWriter();

            var enumeratorType = typeof(RelationEnumerator<>).MakeGenericType(_clientType);
            keyWriter.WriteByteArrayRaw(Prefix);
            var enumerator = (IEnumerator) Activator.CreateInstance(enumeratorType, tr, this,
                keyWriter.GetDataAndRewind().ToAsyncSafe(), new SimpleModificationCounter(), 0);

            var keySavers = new Action<IInternalObjectDBTransaction, AbstractBufferedWriter, object>[indexes.Count];

            for (int i = 0; i < indexes.Count; i++)
            {
                keySavers[i] = CreateSaver(ClientRelationVersionInfo.GetSecondaryKeyFields(indexes[i].Key),
                    $"Relation_{Name}_Upgrade_SK_{indexes[i].Value.Name}_KeySaver");
            }

            while (enumerator.MoveNext())
            {
                var obj = enumerator.Current;

                tr.TransactionProtector.Start();
                tr.KeyValueDBTransaction.SetKeyPrefix(ObjectDB.AllRelationsSKPrefix);

                for (int i = 0; i < indexes.Count; i++)
                {
                    keyWriter.WriteVUInt32(Id);
                    keyWriter.WriteVUInt32(indexes[i].Key);
                    keySavers[i](tr, keyWriter, obj);
                    var keyBytes = keyWriter.GetDataAndRewind();

                    if (!tr.KeyValueDBTransaction.CreateOrUpdateKeyValue(keyBytes, ByteBuffer.NewEmpty()))
                        throw new BTDBException("Internal error, secondary key bytes must be always unique.");
                }
            }
        }

        void LoadUnresolvedVersionInfos(IKeyValueDBTransaction tr)
        {
            LastPersistedVersion = 0;
            var writer = new ByteBufferWriter();
            writer.WriteByteArrayRaw(ObjectDB.RelationVersionsPrefix);
            writer.WriteVUInt32(_id);
            tr.SetKeyPrefix(writer.Data);
            var relationVersions = new Dictionary<uint, RelationVersionInfo>();
            if (tr.FindFirstKey())
            {
                var keyReader = new KeyValueDBKeyReader(tr);
                var valueReader = new KeyValueDBValueReader(tr);
                do
                {
                    keyReader.Restart();
                    valueReader.Restart();
                    LastPersistedVersion = keyReader.ReadVUInt32();
                    var relationVersionInfo = RelationVersionInfo.LoadUnresolved(valueReader, _name);
                    relationVersions[LastPersistedVersion] = relationVersionInfo;
                } while (tr.FindNextKey());
            }

            _relationVersions = new RelationVersionInfo[LastPersistedVersion + 2];
            foreach (var (key, value) in relationVersions)
            {
                _relationVersions[key] = value;
            }
            _valueIDictFinders = new Action<IInternalObjectDBTransaction, AbstractBufferedReader, IList<ulong>>?[_relationVersions.Length];
        }

        void ResolveVersionInfos()
        {
            for (var i = 0; i < _relationVersions.Length; i++)
            {
                if (i == ClientTypeVersion)
                    continue;
                _relationVersions[i]?.ResolveFieldHandlers(_relationInfoResolver.FieldHandlerFactory);
            }
        }

        internal uint Id => _id;

        internal string Name => _name;

        internal Type ClientType => _clientType;

        internal Type InterfaceType => _interfaceType;

        internal object DefaultClientObject => _defaultClientObject;

        internal RelationVersionInfo ClientRelationVersionInfo { get; }

        internal uint LastPersistedVersion { get; set; }

        internal uint ClientTypeVersion { get; }

        internal IDictionary<string, MethodInfo> ApartFields { get; }

        void CreateCreatorLoadersAndSavers()
        {
            _valueSaver = CreateSaver(ClientRelationVersionInfo.GetValueFields(), $"RelationValueSaver_{Name}");
            _primaryKeysSaver = CreateSaverWithApartFields(ClientRelationVersionInfo.GetPrimaryKeyFields(),
                $"RelationKeySaver_{Name}");
            BuildIndexToItemLoaderInfos(_clientType);
            if (ClientRelationVersionInfo.SecondaryKeys.Count > 0)
            {
                _secondaryKeysSavers =
                    new Action<IInternalObjectDBTransaction, AbstractBufferedWriter, object, object>
                        [ClientRelationVersionInfo.SecondaryKeys.Keys.Max() + 1];
                foreach (var (idx, secondaryKeyInfo) in ClientRelationVersionInfo.SecondaryKeys)
                {
                    _secondaryKeysSavers[idx] = CreateSaverWithApartFields(
                        ClientRelationVersionInfo.GetSecondaryKeyFields(idx),
                        $"Relation_{Name}_SK_{secondaryKeyInfo.Name}_KeySaver");
                }
            }
        }

        internal Action<IInternalObjectDBTransaction, AbstractBufferedWriter, object> ValueSaver => _valueSaver;

        internal Action<IInternalObjectDBTransaction, AbstractBufferedWriter, object, object> PrimaryKeysSaver =>
            _primaryKeysSaver;

        void CreateSaverIl(IILGen ilGen, IEnumerable<TableFieldInfo> fields,
            Action<IILGen> pushInstance, Action<IILGen> pushRelationIface,
            Action<IILGen> pushWriter, Action<IILGen> pushTransaction)
        {
            var writerCtxLocal = CreateWriterCtx(ilGen, fields, pushWriter, pushTransaction);
            var props = ClientType.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            foreach (var field in fields)
            {
                var getter = props.First(p => GetPersistentName(p) == field.Name).GetGetMethod(true);
                Action<IILGen> writerOrCtx;
                var handler = field.Handler.SpecializeSaveForType(getter.ReturnType);
                if (handler.NeedsCtx())
                    writerOrCtx = il => il.Ldloc(writerCtxLocal);
                else
                    writerOrCtx = pushWriter;
                MethodInfo apartFieldGetter = null;
                if (pushRelationIface != null)
                    ApartFields.TryGetValue(field.Name, out apartFieldGetter);
                handler.Save(ilGen, writerOrCtx, il =>
                {
                    if (apartFieldGetter != null)
                    {
                        il.Do(pushRelationIface);
                        getter = apartFieldGetter;
                    }
                    else
                    {
                        il.Do(pushInstance);
                    }

                    il.Callvirt(getter);
                    _relationInfoResolver.TypeConvertorGenerator.GenerateConversion(getter.ReturnType,
                        handler.HandledType())(il);
                });
            }
        }

        static IILLocal CreateWriterCtx(IILGen ilGenerator, IEnumerable<TableFieldInfo> fields,
            Action<IILGen> pushWriter, Action<IILGen> pushTransaction)
        {
            var anyNeedsCtx = fields.Any(tfi => tfi.Handler.NeedsCtx());
            IILLocal writerCtxLocal = null;
            if (anyNeedsCtx)
            {
                writerCtxLocal = ilGenerator.DeclareLocal(typeof(IWriterCtx));
                ilGenerator
                    .Do(pushTransaction)
                    .Do(pushWriter)
                    .Newobj(() => new DBWriterCtx(null, null))
                    .Stloc(writerCtxLocal);
            }

            return writerCtxLocal;
        }

        static void StoreNthArgumentOfTypeIntoLoc(IILGen il, ushort argIdx, Type type, ushort locIdx)
        {
            il
                .Ldarg(argIdx)
                .Castclass(type)
                .Stloc(locIdx);
        }

        struct LocalAndHandler
        {
            public IILLocal Local;
            public IFieldHandler Handler;
        }

        Action<IInternalObjectDBTransaction, AbstractBufferedWriter, AbstractBufferedReader, AbstractBufferedReader,
            object> CreateBytesToSKSaver(
            uint version, uint secondaryKeyIndex, string saverName)
        {
            var method =
                ILBuilder.Instance
                    .NewMethod<Action<IInternalObjectDBTransaction, AbstractBufferedWriter, AbstractBufferedReader,
                        AbstractBufferedReader, object>>(saverName);
            var ilGenerator = method.Generator;
            IILLocal defaultObjectLoc = null;
            Action<IILGen> pushWriter = il => il.Ldarg(1);

            var firstBuffer = new BufferInfo(); //pk's
            var secondBuffer = new BufferInfo(); //values
            var outOfOrderSkParts = new Dictionary<int, LocalAndHandler>(); //local and specialized saver

            var pks = ClientRelationVersionInfo.GetPrimaryKeyFields().ToList();
            var skFieldIds = ClientRelationVersionInfo.SecondaryKeys[secondaryKeyIndex].Fields.ToList();
            var skFields = ClientRelationVersionInfo.GetSecondaryKeyFields(secondaryKeyIndex).ToList();
            var valueFields = _relationVersions[version].GetValueFields().ToList();
            var writerCtxLocal = CreateWriterCtx(ilGenerator, skFields, pushWriter, il => il.Ldarg(0));
            for (var skFieldIdx = 0; skFieldIdx < skFieldIds.Count; skFieldIdx++)
            {
                LocalAndHandler saveLocalInfo;
                if (outOfOrderSkParts.TryGetValue(skFieldIdx, out saveLocalInfo))
                {
                    var writerOrCtx = WriterOrContextForHandler(saveLocalInfo.Handler, writerCtxLocal, pushWriter);
                    saveLocalInfo.Handler.Save(ilGenerator, writerOrCtx, il => il.Ldloc(saveLocalInfo.Local));
                    continue;
                }

                var skf = skFieldIds[skFieldIdx];
                if (skf.IsFromPrimaryKey)
                {
                    InitializeBuffer(2, ref firstBuffer, ilGenerator, pks, true);
                    //firstBuffer.ActualFieldIdx == number of processed PK's
                    for (var pkIdx = firstBuffer.ActualFieldIdx; pkIdx < skf.Index; pkIdx++)
                    {
                        //all PK parts are contained in SK
                        int skFieldIdxForPk, tmp;
                        FindPosition(pkIdx, skFieldIds, 0, out tmp, out skFieldIdxForPk);
                        StoreIntoLocal(ilGenerator, pks[pkIdx].Handler, firstBuffer, outOfOrderSkParts, skFieldIdxForPk,
                            skFields[skFieldIdxForPk].Handler);
                    }

                    CopyToOutput(ilGenerator, pks[(int) skf.Index].Handler, writerCtxLocal, pushWriter,
                        skFields[skFieldIdx].Handler, firstBuffer);
                    firstBuffer.ActualFieldIdx = (int) skf.Index + 1;
                }
                else
                {
                    InitializeBuffer(3, ref secondBuffer, ilGenerator, valueFields, false);

                    var valueFieldIdx = valueFields.FindIndex(tfi => tfi.Name == skFields[skFieldIdx].Name);
                    if (valueFieldIdx >= 0)
                    {
                        for (var valueIdx = secondBuffer.ActualFieldIdx; valueIdx < valueFieldIdx; valueIdx++)
                        {
                            var valueField = valueFields[valueIdx];
                            var storeForSkIndex = skFields.FindIndex(skFieldIdx, fi => fi.Name == valueField.Name);
                            if (storeForSkIndex == -1)
                                valueField.Handler.Skip(ilGenerator,
                                    valueField.Handler.NeedsCtx() ? secondBuffer.PushCtx : secondBuffer.PushReader);
                            else
                                StoreIntoLocal(ilGenerator, valueField.Handler, secondBuffer, outOfOrderSkParts,
                                    storeForSkIndex, skFields[storeForSkIndex].Handler);
                        }

                        CopyToOutput(ilGenerator, valueFields[valueFieldIdx].Handler, writerCtxLocal, pushWriter,
                            skFields[skFieldIdx].Handler, secondBuffer);
                        secondBuffer.ActualFieldIdx = valueFieldIdx + 1;
                    }
                    else
                    {
                        //older version of value does not contain sk field - store field from default value (can be initialized in constructor)
                        if (defaultObjectLoc == null)
                        {
                            defaultObjectLoc = ilGenerator.DeclareLocal(ClientType);
                            ilGenerator.Ldarg(4)
                                .Castclass(ClientType)
                                .Stloc(defaultObjectLoc);
                        }

                        var loc = defaultObjectLoc;
                        CreateSaverIl(ilGenerator,
                            new[] {ClientRelationVersionInfo.GetSecondaryKeyField((int) skf.Index)},
                            il => il.Ldloc(loc), null, pushWriter, il => il.Ldarg(0));
                    }
                }
            }

            ilGenerator.Ret();
            return method.Create();
        }

        static void CopyToOutput(IILGen ilGenerator, IFieldHandler valueHandler, IILLocal writerCtxLocal,
            Action<IILGen> pushWriter,
            IFieldHandler skHandler, BufferInfo buffer)

        {
            var writerOrCtx = WriterOrContextForHandler(valueHandler, writerCtxLocal, pushWriter);
            skHandler.SpecializeSaveForType(valueHandler.HandledType()).Save(ilGenerator, writerOrCtx,
                il =>
                {
                    valueHandler.Load(ilGenerator, valueHandler.NeedsCtx() ? buffer.PushCtx : buffer.PushReader);
                });
        }

        static void StoreIntoLocal(IILGen ilGenerator, IFieldHandler valueHandler, BufferInfo bufferInfo,
            Dictionary<int, LocalAndHandler> outOfOrderSkParts, int skFieldIdx, IFieldHandler skFieldHandler)
        {
            var local = ilGenerator.DeclareLocal(valueHandler.HandledType());
            valueHandler.Load(ilGenerator, valueHandler.NeedsCtx() ? bufferInfo.PushCtx : bufferInfo.PushReader);
            ilGenerator.Stloc(local);
            outOfOrderSkParts[skFieldIdx] = new LocalAndHandler
            {
                Handler = skFieldHandler.SpecializeSaveForType(valueHandler.HandledType()),
                Local = local
            };
        }

        static Action<IILGen> WriterOrContextForHandler(IFieldHandler handler, IILLocal writerCtxLocal,
            Action<IILGen> pushWriter)
        {
            Action<IILGen> writerOrCtx;
            if (handler.NeedsCtx())
            {
                writerOrCtx = il => il.Ldloc(writerCtxLocal);
            }
            else
            {
                writerOrCtx = pushWriter;
            }

            return writerOrCtx;
        }

        static void InitializeBuffer(ushort bufferArgIdx, ref BufferInfo bufferInfo, IILGen ilGenerator,
            List<TableFieldInfo> fields, bool skipAllRelationsPKPrefix)
        {
            if (bufferInfo.ReaderCreated) return;
            bufferInfo.ReaderCreated = true;
            bufferInfo.PushReader = il => il.Ldarg(bufferArgIdx);

            if (skipAllRelationsPKPrefix)
                ilGenerator
                    .Do(bufferInfo.PushReader)
                    .Call(() => default(AbstractBufferedReader).SkipInt8()); //ObjectDB.AllRelationsPKPrefix
            ilGenerator
                .Do(bufferInfo.PushReader).Call(() => default(AbstractBufferedReader).SkipVUInt32());

            if (fields.Any(tfi => tfi.Handler.NeedsCtx()))
            {
                var readerCtxLocal = ilGenerator.DeclareLocal(typeof(IReaderCtx));
                ilGenerator
                    .Ldarg(0) //tr
                    .Ldarg(bufferArgIdx)
                    .Newobj(() => new DBReaderCtx(null, null))
                    .Stloc(readerCtxLocal);
                bufferInfo.PushCtx = il => il.Ldloc(readerCtxLocal);
            }
        }

        Action<IInternalObjectDBTransaction, AbstractBufferedWriter, object, object> CreateSaverWithApartFields(
            IEnumerable<TableFieldInfo> fields, string saverName)
        {
            var method = ILBuilder.Instance.NewMethod<
                Action<IInternalObjectDBTransaction, AbstractBufferedWriter, object, object>>(saverName);
            var ilGenerator = method.Generator;
            ilGenerator.DeclareLocal(ClientType);
            StoreNthArgumentOfTypeIntoLoc(ilGenerator, 2, ClientType, 0);
            var hasApartFields = ApartFields.Any();
            if (hasApartFields)
            {
                ilGenerator.DeclareLocal(_interfaceType);
                StoreNthArgumentOfTypeIntoLoc(ilGenerator, 3, _interfaceType, 1);
            }

            CreateSaverIl(ilGenerator, fields,
                il => il.Ldloc(0), hasApartFields ? il => il.Ldloc(1) : (Action<IILGen>) null,
                il => il.Ldarg(1), il => il.Ldarg(0));
            ilGenerator
                .Ret();
            return method.Create();
        }

        Action<IInternalObjectDBTransaction, AbstractBufferedWriter, object> CreateSaver(
            IReadOnlyCollection<TableFieldInfo> fields, string saverName)

        {
            var method =
                ILBuilder.Instance.NewMethod<Action<IInternalObjectDBTransaction, AbstractBufferedWriter, object>>(
                    saverName);
            var ilGenerator = method.Generator;
            ilGenerator.DeclareLocal(ClientType);
            StoreNthArgumentOfTypeIntoLoc(ilGenerator, 2, ClientType, 0);
            CreateSaverIl(ilGenerator, fields,
                il => il.Ldloc(0), null, il => il.Ldarg(1), il => il.Ldarg(0));
            ilGenerator
                .Ret();
            return method.Create();
        }


        RelationVersionInfo CreateVersionInfoByReflection()
        {
            var props = _clientType.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            var primaryKeys = new Dictionary<uint, TableFieldInfo>(1); //PK order->fieldInfo
            var secondaryKeyFields = new List<TableFieldInfo>();
            var secondaryKeys =
                new List<Tuple<int, IList<SecondaryKeyAttribute>>
                >(); //positive: sec key field idx, negative: pk order, attrs

            var fields = new List<TableFieldInfo>(props.Length);
            foreach (var pi in props)
            {
                if (pi.GetCustomAttributes(typeof(NotStoredAttribute), true).Length != 0) continue;
                if (pi.GetIndexParameters().Length != 0) continue;
                var pks = pi.GetCustomAttributes(typeof(PrimaryKeyAttribute), true);
                PrimaryKeyAttribute actualPKAttribute = null;
                if (pks.Length != 0)
                {
                    actualPKAttribute = (PrimaryKeyAttribute) pks[0];
                    var fieldInfo = TableFieldInfo.Build(Name, pi, _relationInfoResolver.FieldHandlerFactory,
                        FieldHandlerOptions.Orderable);
                    if (fieldInfo.Handler.NeedsCtx())
                        throw new BTDBException($"Unsupported key field {fieldInfo.Name} type.");
                    primaryKeys.Add(actualPKAttribute.Order, fieldInfo);
                }

                var sks = pi.GetCustomAttributes(typeof(SecondaryKeyAttribute), true);
                var id = (int) (-actualPKAttribute?.Order ?? secondaryKeyFields.Count);
                List<SecondaryKeyAttribute> currentList = null;
                for (var i = 0; i < sks.Length; i++)
                {
                    if (i == 0)
                    {
                        currentList = new List<SecondaryKeyAttribute>(sks.Length);
                        secondaryKeys.Add(new Tuple<int, IList<SecondaryKeyAttribute>>(id, currentList));
                        if (actualPKAttribute == null)
                            secondaryKeyFields.Add(TableFieldInfo.Build(Name, pi,
                                _relationInfoResolver.FieldHandlerFactory, FieldHandlerOptions.Orderable));
                    }

                    var key = (SecondaryKeyAttribute) sks[i];
                    if (key.Name == "Id")
                        throw new BTDBException(
                            "'Id' is invalid name for secondary key, it is reserved for primary key identification.");
                    currentList.Add(key);
                }

                if (actualPKAttribute == null)
                    fields.Add(TableFieldInfo.Build(Name, pi, _relationInfoResolver.FieldHandlerFactory,
                        FieldHandlerOptions.None));
            }

            var prevVersion = LastPersistedVersion > 0 ? _relationVersions[LastPersistedVersion] : null;
            return new RelationVersionInfo(primaryKeys, secondaryKeys, secondaryKeyFields.ToArray(), fields.ToArray(),
                prevVersion);
        }

        static IDictionary<string, MethodInfo> FindApartFields(MethodInfo[] methods, PropertyInfo[] properties,
            RelationVersionInfo versionInfo)

        {
            var result = new Dictionary<string, MethodInfo>();
            var pks = versionInfo.GetPrimaryKeyFields().ToDictionary(tfi => tfi.Name, tfi => tfi);
            foreach (var method in methods)
            {
                if (!method.Name.StartsWith("get_"))
                    continue;
                var name = GetPersistentName(method.Name.Substring(4), properties);
                if (!pks.TryGetValue(name, out var tfi))
                    throw new BTDBException($"Property {name} is not part of primary key.");
                if (!tfi.Handler.IsCompatibleWith(method.ReturnType, FieldHandlerOptions.Orderable))
                    throw new BTDBException(
                        $"Property {name} has incompatible return type with the member of primary key with the same name.");
                result.Add(name, method);
            }

            return result;
        }

        static bool IsIgnoredType(Type type)
        {
            if (type.IsGenericType)
            {
                var genericTypeDefinition = type.GetGenericTypeDefinition();
                if (genericTypeDefinition == typeof(IEnumerable<>) ||
                    genericTypeDefinition == typeof(IReadOnlyCollection<>))
                    return true;
            }
            else
            {
                if (type == typeof(IEnumerable))
                    return true;
            }

            return false;
        }

        public static IEnumerable<MethodInfo> GetMethods(Type interfaceType)
        {
            if (IsIgnoredType(interfaceType)) yield break;
            var methods = interfaceType.GetMethods();
            foreach (var method in methods)
                yield return method;
            foreach (var iface in interfaceType.GetInterfaces())
            {
                if (IsIgnoredType(iface)) continue;
                var inheritedMethods = iface.GetMethods();
                foreach (var method in inheritedMethods)
                    yield return method;
            }
        }

        public static IEnumerable<PropertyInfo> GetProperties(Type interfaceType)
        {
            if (IsIgnoredType(interfaceType)) yield break;
            var properties = interfaceType.GetProperties();
            foreach (var property in properties)
                yield return property;
            foreach (var iface in interfaceType.GetInterfaces())
            {
                if (IsIgnoredType(iface)) continue;
                var inheritedProperties = iface.GetProperties();
                foreach (var property in inheritedProperties)
                    yield return property;
            }
        }

        internal Action<IInternalObjectDBTransaction, AbstractBufferedReader, IList<ulong>> GetIDictFinder(uint version)
        {
            Action<IInternalObjectDBTransaction, AbstractBufferedReader, IList<ulong>>? res;
            do
            {
                res = _valueIDictFinders[version];
                if (res != null) return res;
                res = CreateIDictFinder(version);
            } while (Interlocked.CompareExchange(ref _valueIDictFinders[version], res, null) != null);

            return res;
        }

        internal Action<IInternalObjectDBTransaction, AbstractBufferedWriter, object, object> GetSecondaryKeysKeySaver
            (uint secondaryKeyIndex)
        {
            return _secondaryKeysSavers[secondaryKeyIndex];
        }

        internal Action<IInternalObjectDBTransaction, AbstractBufferedWriter, AbstractBufferedReader,
                AbstractBufferedReader, object> GetPKValToSKMerger
            (uint version, uint secondaryKeyIndex)
        {
            var h = secondaryKeyIndex + version * 10000ul;
            return _secondaryKeysConvertSavers.GetOrAdd(h,
                (_, ver, secKeyIndex, relationInfo) => CreateBytesToSKSaver(ver, secKeyIndex,
                    $"Relation_{relationInfo.Name}_PkVal_to_SK_{relationInfo.ClientRelationVersionInfo.SecondaryKeys[secKeyIndex].Name}_v{ver}"),
                version, secondaryKeyIndex, this);
        }

        //takes secondaryKey key & value bytes and restores primary key bytes
        public Action<AbstractBufferedReader, AbstractBufferedReader, AbstractBufferedWriter> GetSKKeyValuetoPKMerger
            (uint secondaryKeyIndex, uint paramFieldCountInFirstBuffer)
        {
            var h = 10000ul * secondaryKeyIndex + paramFieldCountInFirstBuffer;
            return _secondaryKeyValuetoPKLoader.GetOrAdd(h,
                (_, secKeyIndex, relationInfo, paramFieldCount) => relationInfo.CreatePrimaryKeyFromSKDataMerger(
                    secKeyIndex, paramFieldCount,
                    $"Relation_SK_to_PK_{relationInfo.ClientRelationVersionInfo.SecondaryKeys[secKeyIndex].Name}_p{paramFieldCount}"),
                secondaryKeyIndex, this, (int) paramFieldCountInFirstBuffer);
        }

        struct MemorizedPositionWithLength
        {
            public int BufferIndex { get; set; } //0 first, 1 second
            public IILLocal Pos { get; set; } // IMemorizedPosition
            public IILLocal Length { get; set; } // int
        }

        struct BufferInfo
        {
            public bool ReaderCreated;
            public Action<IILGen> PushReader;
            public Action<IILGen> PushCtx;
            public int ActualFieldIdx;
        }

        Action<AbstractBufferedReader, AbstractBufferedReader, AbstractBufferedWriter> CreatePrimaryKeyFromSKDataMerger(
            uint secondaryKeyIndex,
            int paramFieldCountInFirstBuffer, string mergerName)
        {
            var method =
                ILBuilder.Instance
                    .NewMethod<Action<AbstractBufferedReader, AbstractBufferedReader, AbstractBufferedWriter>>(
                        mergerName);
            var ilGenerator = method.Generator;

            Action<IILGen> pushWriter = il => il.Ldarg(2);
            var skFields = ClientRelationVersionInfo.SecondaryKeys[secondaryKeyIndex].Fields;

            var positionLoc = ilGenerator.DeclareLocal(typeof(ulong)); //stored position
            var memoPositionLoc = ilGenerator.DeclareLocal(typeof(IMemorizedPosition));

            var firstBuffer = new BufferInfo();
            var secondBuffer = new BufferInfo {ActualFieldIdx = paramFieldCountInFirstBuffer};
            var outOfOrderPKParts =
                new Dictionary<int, MemorizedPositionWithLength>(); //index -> bufferIdx, IMemorizedPosition, length

            var pks = ClientRelationVersionInfo.GetPrimaryKeyFields().ToList();
            for (var pkIdx = 0; pkIdx < pks.Count; pkIdx++)
            {
                if (outOfOrderPKParts.ContainsKey(pkIdx))
                {
                    var memo = outOfOrderPKParts[pkIdx];
                    var pushReader = GetBufferPushAction(memo.BufferIndex, firstBuffer.PushReader,
                        secondBuffer.PushReader);
                    CopyFromMemorizedPosition(ilGenerator, pushReader, pushWriter, memo, memoPositionLoc);
                    continue;
                }

                FindPosition(pkIdx, skFields, paramFieldCountInFirstBuffer, out var bufferIdx, out var skFieldIdx);
                if (bufferIdx == 0)
                {
                    MergerInitializeFirstBufferReader(ilGenerator, ref firstBuffer);
                    CopyFromBuffer(ilGenerator, bufferIdx, skFieldIdx, ref firstBuffer, outOfOrderPKParts, pks,
                        skFields, positionLoc,
                        memoPositionLoc, pushWriter);
                }
                else
                {
                    MergerInitializeBufferReader(ref secondBuffer, 1);
                    CopyFromBuffer(ilGenerator, bufferIdx, skFieldIdx, ref secondBuffer, outOfOrderPKParts, pks,
                        skFields, positionLoc,
                        memoPositionLoc, pushWriter);
                }
            }

            ilGenerator.Ret();
            return method.Create();
        }

        void CopyFromBuffer(IILGen ilGenerator, int bufferIdx, int skFieldIdx, ref BufferInfo bi,
            Dictionary<int, MemorizedPositionWithLength> outOfOrderPKParts,
            List<TableFieldInfo> pks, IList<FieldId> skFields, IILLocal positionLoc, IILLocal memoPositionLoc,
            Action<IILGen> pushWriter)
        {
            for (var idx = bi.ActualFieldIdx; idx < skFieldIdx; idx++)
            {
                var field = skFields[idx];
                if (field.IsFromPrimaryKey)
                {
                    outOfOrderPKParts[(int) field.Index] = SkipWithMemorizing(bufferIdx, ilGenerator, bi.PushReader,
                        pks[(int) field.Index].Handler, positionLoc);
                }
                else
                {
                    var f = ClientRelationVersionInfo.GetSecondaryKeyField((int) field.Index);
                    f.Handler.Skip(ilGenerator, bi.PushReader);
                }
            }

            var skField = skFields[skFieldIdx];
            GenerateCopyFieldFromByteBufferToWriterIl(ilGenerator, pks[(int) skField.Index].Handler, bi.PushReader,
                pushWriter, positionLoc, memoPositionLoc);

            bi.ActualFieldIdx = skFieldIdx + 1;
        }

        static void FindPosition(int pkIdx, IList<FieldId> skFields, int paramFieldCountInFirstBuffer,
            out int bufferIdx, out int skFieldIdx)
        {
            for (var i = 0; i < skFields.Count; i++)
            {
                var field = skFields[i];
                if (!field.IsFromPrimaryKey) continue;
                if (field.Index != pkIdx) continue;
                skFieldIdx = i;
                bufferIdx = i < paramFieldCountInFirstBuffer ? 0 : 1;
                return;
            }

            throw new BTDBException("Secondary key relation processing error.");
        }

        static void MergerInitializeBufferReader(ref BufferInfo bi, ushort arg)
        {
            if (bi.ReaderCreated)
                return;
            bi.ReaderCreated = true;
            bi.PushReader = il => il.Ldarg(arg);
        }

        static void MergerInitializeFirstBufferReader(IILGen ilGenerator, ref BufferInfo bi)
        {
            if (bi.ReaderCreated)
                return;
            MergerInitializeBufferReader(ref bi, 0);
            ilGenerator
                //skip all relations
                .Do(bi.PushReader)
                .LdcI4(ObjectDB.AllRelationsSKPrefix.Length)
                .Callvirt(() => default(AbstractBufferedReader).SkipBlock(0))
                //skip relation id
                .Do(bi.PushReader).Call(() => default(AbstractBufferedReader).SkipVUInt32())
                //skip secondary key index
                .Do(bi.PushReader).Call(() => default(AbstractBufferedReader).SkipVUInt32());
        }


        Action<IILGen> GetBufferPushAction(int bufferIndex, Action<IILGen> pushReaderFirst,
            Action<IILGen> pushReaderSecond)
        {
            return bufferIndex == 0 ? pushReaderFirst : pushReaderSecond;
        }

        MemorizedPositionWithLength SkipWithMemorizing(int activeBuffer, IILGen ilGenerator, Action<IILGen> pushReader,
            IFieldHandler handler, IILLocal tempPosition)
        {
            var memoPos = ilGenerator.DeclareLocal(typeof(IMemorizedPosition));
            var memoLen = ilGenerator.DeclareLocal(typeof(int));
            var position = new MemorizedPositionWithLength
                {BufferIndex = activeBuffer, Pos = memoPos, Length = memoLen};
            MemorizeCurrentPosition(ilGenerator, pushReader, memoPos);
            StoreCurrentPosition(ilGenerator, pushReader, tempPosition);
            handler.Skip(ilGenerator, pushReader);
            ilGenerator
                .Do(pushReader) //[VR]
                .Callvirt(() => default(AbstractBufferedReader).GetCurrentPosition()) //[posNew];
                .Ldloc(tempPosition) //[posNew, posOld]
                .Sub() //[readLen]
                .ConvI4() //[readLen(i)]
                .Stloc(memoLen); //[]
            return position;
        }

        void CopyFromMemorizedPosition(IILGen ilGenerator, Action<IILGen> pushReader, Action<IILGen> pushWriter,
            MemorizedPositionWithLength memo,
            IILLocal memoPositionLoc)
        {
            MemorizeCurrentPosition(ilGenerator, pushReader, memoPositionLoc);
            ilGenerator
                .Do(pushWriter) //[W]
                .Do(pushReader) //[W,VR]
                .Ldloc(memo.Length) //[W, VR, readLen]
                .Ldloc(memo.Pos) //[W, VR, readLen, Memorize]
                .Callvirt(() => default(IMemorizedPosition).Restore()) //[W, VR]
                .Call(() => default(AbstractBufferedReader).ReadByteArrayRaw(0)) //[W, byte[]]
                .Call(() => default(AbstractBufferedWriter).WriteByteArrayRaw(null)) //[]
                .Ldloc(memoPositionLoc) //[Memorize]
                .Callvirt(() => default(IMemorizedPosition).Restore()); //[]
        }

        void MemorizeCurrentPosition(IILGen ilGenerator, Action<IILGen> pushReader, IILLocal memoPositionLoc)
        {
            ilGenerator
                .Do(pushReader)
                .Castclass(typeof(ByteBufferReader))
                .Call(() => default(ByteBufferReader).MemorizeCurrentPosition())
                .Stloc(memoPositionLoc);
        }

        void StoreCurrentPosition(IILGen ilGenerator, Action<IILGen> pushReader, IILLocal positionLoc)
        {
            ilGenerator
                .Do(pushReader)
                .Callvirt(() => default(AbstractBufferedReader).GetCurrentPosition())
                .Stloc(positionLoc);
        }

        void GenerateCopyFieldFromByteBufferToWriterIl(IILGen ilGenerator, IFieldHandler handler,
            Action<IILGen> pushReader,
            Action<IILGen> pushWriter, IILLocal positionLoc, IILLocal memoPositionLoc)
        {
            MemorizeCurrentPosition(ilGenerator, pushReader, memoPositionLoc);
            StoreCurrentPosition(ilGenerator, pushReader, positionLoc);

            handler.Skip(ilGenerator, pushReader);

            ilGenerator
                .Do(pushWriter) //[W]
                .Do(pushReader) //[W,VR]
                .Dup() //[W, VR, VR]
                .Callvirt(() => default(AbstractBufferedReader).GetCurrentPosition()) //[W, VR, posNew];
                .Ldloc(positionLoc) //[W, VR, posNew, posOld]
                .Sub() //[W, VR, readLen]
                .ConvI4() //[W, VR, readLen(i)]
                .Ldloc(memoPositionLoc) //[W, VR, readLen, Memorize]
                .Callvirt(() => default(IMemorizedPosition).Restore()) //[W, VR, readLen]
                .Call(() => default(AbstractBufferedReader).ReadByteArrayRaw(0)) //[W, byte[]]
                .Call(() => default(AbstractBufferedWriter).WriteByteArrayRaw(null)); //[]
        }

        public object GetSimpleLoader(SimpleLoaderType handler)
        {
            return _simpleLoader.GetOrAdd(handler, CreateSimpleLoader);
        }

        object CreateSimpleLoader(SimpleLoaderType loaderType)
        {
            var delegateType = typeof(Func<,,>).MakeGenericType(typeof(AbstractBufferedReader), typeof(IReaderCtx),
                loaderType.RealType);
            var dm = ILBuilder.Instance.NewMethod(loaderType.FieldHandler.Name + "SimpleReader", delegateType);
            var ilGenerator = dm.Generator;
            Action<IILGen> pushReaderOrCtx = il => il.Ldarg((ushort) (loaderType.FieldHandler.NeedsCtx() ? 1 : 0));
            loaderType.FieldHandler.Load(ilGenerator, pushReaderOrCtx);
            ilGenerator
                .Do(_typeConvertorGenerator.GenerateConversion(loaderType.FieldHandler.HandledType(),
                    loaderType.RealType))
                .Ret();
            return dm.Create();
        }

        Action<IInternalObjectDBTransaction, AbstractBufferedReader, object> CreateLoader(uint version,
            IEnumerable<TableFieldInfo> fields, string loaderName)
        {
            var method =
                ILBuilder.Instance.NewMethod<Action<IInternalObjectDBTransaction, AbstractBufferedReader, object>>(
                    loaderName);
            var ilGenerator = method.Generator;
            ilGenerator.DeclareLocal(ClientType);
            ilGenerator
                .Ldarg(2)
                .Castclass(ClientType)
                .Stloc(0);
            var relationVersionInfo = _relationVersions[version];
            var clientRelationVersionInfo = ClientRelationVersionInfo;
            var anyNeedsCtx = relationVersionInfo!.NeedsCtx() || clientRelationVersionInfo.NeedsCtx();
            if (anyNeedsCtx)
            {
                ilGenerator.DeclareLocal(typeof(IReaderCtx));
                ilGenerator
                    .Ldarg(0)
                    .Ldarg(1)
                    .Newobj(() => new DBReaderCtx(null, null))
                    .Stloc(1);
            }

            var props = _clientType.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            foreach (var srcFieldInfo in fields)
            {
                Action<IILGen> readerOrCtx;
                if (srcFieldInfo.Handler.NeedsCtx())
                    readerOrCtx = il => il.Ldloc(1);
                else
                    readerOrCtx = il => il.Ldarg(1);
                var destFieldInfo = clientRelationVersionInfo[srcFieldInfo.Name];
                if (destFieldInfo != null)
                {
                    var fieldInfo = props.First(p => GetPersistentName(p) == destFieldInfo.Name).GetSetMethod(true);
                    var fieldType = fieldInfo!.GetParameters()[0].ParameterType;
                    var specializedSrcHandler =
                        srcFieldInfo.Handler.SpecializeLoadForType(fieldType, destFieldInfo.Handler!);
                    var willLoad = specializedSrcHandler.HandledType();
                    var converterGenerator =
                        _relationInfoResolver.TypeConvertorGenerator.GenerateConversion(willLoad, fieldType);
                    if (converterGenerator != null)
                    {
                        ilGenerator.Ldloc(0);
                        specializedSrcHandler.Load(ilGenerator, readerOrCtx);
                        converterGenerator(ilGenerator);
                        ilGenerator.Call(fieldInfo);
                        continue;
                    }
                }

                srcFieldInfo.Handler.Skip(ilGenerator, readerOrCtx);
            }

            if (ClientTypeVersion != version)
            {
                foreach (var srcFieldInfo in clientRelationVersionInfo.GetValueFields())
                {
                    var iFieldHandlerWithInit = srcFieldInfo.Handler as IFieldHandlerWithInit;
                    if (iFieldHandlerWithInit == null) continue;
                    if (relationVersionInfo[srcFieldInfo.Name] != null) continue;
                    Action<IILGen> readerOrCtx;
                    if (srcFieldInfo.Handler.NeedsCtx())
                        readerOrCtx = il => il.Ldloc(1);
                    else
                        readerOrCtx = il => il.Ldnull();
                    var specializedSrcHandler = srcFieldInfo.Handler;
                    var willLoad = specializedSrcHandler.HandledType();
                    var setterMethod = props.First(p => GetPersistentName(p) == srcFieldInfo.Name).GetSetMethod(true);
                    var converterGenerator =
                        _relationInfoResolver.TypeConvertorGenerator.GenerateConversion(willLoad,
                            setterMethod.GetParameters()[0].ParameterType);
                    if (converterGenerator == null) continue;
                    if (!iFieldHandlerWithInit.NeedInit()) continue;
                    ilGenerator.Ldloc(0);
                    iFieldHandlerWithInit.Init(ilGenerator, readerOrCtx);
                    converterGenerator(ilGenerator);
                    ilGenerator.Call(setterMethod);
                }
            }

            ilGenerator.Ret();
            return method.Create();
        }

        static string GetPersistentName(PropertyInfo p)
        {
            var a = p.GetCustomAttribute<PersistedNameAttribute>();
            return a != null ? a.Name : p.Name;
        }

        internal static string GetPersistentName(string name, PropertyInfo[] properties)
        {
            foreach (var prop in properties)
            {
                if (prop.Name == name)
                    return GetPersistentName(prop);
            }

            return name;
        }

        public void FreeContent(IInternalObjectDBTransaction tr, ByteBuffer valueBytes)
        {
            FreeContentOldDict.Clear();
            FindUsedObjectsToFree(tr, valueBytes, FreeContentOldDict);

            foreach (var dictId in FreeContentOldDict)
            {
                FreeIDictionary(tr, dictId);
            }
        }

        internal static void FreeIDictionary(IInternalObjectDBTransaction tr, ulong dictId)
        {
            var o = ObjectDB.AllDictionariesPrefix.Length;
            var prefix = new byte[o + PackUnpack.LengthVUInt(dictId)];
            Array.Copy(ObjectDB.AllDictionariesPrefix, prefix, o);
            PackUnpack.PackVUInt(prefix, ref o, dictId);
            tr.TransactionProtector.Start();
            tr.KeyValueDBTransaction.SetKeyPrefixUnsafe(prefix);
            tr.KeyValueDBTransaction.EraseAll();
        }

        public void FindUsedObjectsToFree(IInternalObjectDBTransaction tr, ByteBuffer valueBytes,
            IList<ulong> dictionaries)
        {
            var valueReader = new ByteBufferReader(valueBytes);
            var version = valueReader.ReadVUInt32();
            GetIDictFinder(version)?.Invoke(tr, valueReader, dictionaries);
        }

        Action<IInternalObjectDBTransaction, AbstractBufferedReader, IList<ulong>> CreateIDictFinder(uint version)
        {
            var method = ILBuilder.Instance
                .NewMethod<Action<IInternalObjectDBTransaction, AbstractBufferedReader, IList<ulong>>>(
                    $"Relation{Name}_IDictFinder");
            var ilGenerator = method.Generator;

            var relationVersionInfo = _relationVersions[version];
            var needGenerateFreeFor = 0;
            var fakeMethod = ILBuilder.Instance.NewMethod<Action>("Relation_fake");
            var fakeGenerator = fakeMethod.Generator;
            var valueFields = relationVersionInfo!.GetValueFields().ToArray();
            for (var i = 0; i < valueFields.Length; i++)
            {
                var needsFreeContent = valueFields[i].Handler!.FreeContent(fakeGenerator, _ => { });
                if (needsFreeContent != NeedsFreeContent.No)
                    needGenerateFreeFor = i + 1;
            }

            if (needGenerateFreeFor == 0)
            {
                return null;
            }

            var anyNeedsCtx = relationVersionInfo.GetValueFields().Any(f => f.Handler.NeedsCtx());
            if (anyNeedsCtx)
            {
                ilGenerator.DeclareLocal(typeof(IReaderCtx)); //loc 0
                ilGenerator
                    .Ldarg(0)
                    .Ldarg(1)
                    .Ldarg(2)
                    .Newobj(() => new DBReaderWithFreeInfoCtx(null, null, null))
                    .Stloc(0);
            }

            for (int i = 0; i < needGenerateFreeFor; i++)
            {
                Action<IILGen> readerOrCtx;
                if (valueFields[i].Handler.NeedsCtx())
                    readerOrCtx = il => il.Ldloc(0);
                else
                    readerOrCtx = il => il.Ldarg(1);
                valueFields[i].Handler.FreeContent(ilGenerator, readerOrCtx);
            }

            ilGenerator.Ret();
            return method.Create();
        }

        void CalculatePrefix()
        {
            var o = ObjectDB.AllRelationsPKPrefix.Length;
            var prefix = new byte[o + PackUnpack.LengthVUInt(Id)];
            Array.Copy(ObjectDB.AllRelationsPKPrefix, prefix, o);
            PackUnpack.PackVUInt(prefix, ref o, Id);
            Prefix = prefix;
        }

        public override string ToString()
        {
            return $"{Name} {ClientType} Id:{Id}";
        }
    }

    public class DBReaderWithFreeInfoCtx : DBReaderCtx
    {
        readonly IList<ulong> _freeDictionaries;
        List<bool> _seenObjects;

        public DBReaderWithFreeInfoCtx(IInternalObjectDBTransaction transaction, AbstractBufferedReader reader,
            IList<ulong> freeDictionaries)
            : base(transaction, reader)
        {
            _freeDictionaries = freeDictionaries;
        }

        public IList<ulong> DictIds => _freeDictionaries;

        public override void RegisterDict(ulong dictId)
        {
            _freeDictionaries.Add(dictId);
        }

        public override void FreeContentInNativeObject()
        {
            var id = _reader.ReadVInt64();
            if (id == 0)
            {
            }
            else if (id <= int.MinValue || id > 0)
            {
                _transaction.TransactionProtector.Start();
                _transaction.KeyValueDBTransaction.SetKeyPrefix(ObjectDB.AllObjectsPrefix);
                if (!_transaction.KeyValueDBTransaction.FindExactKey(ObjectDBTransaction.BuildKeyFromOid((ulong) id)))
                    return;
                var reader = new ByteBufferReader(_transaction.KeyValueDBTransaction.GetValue());
                var tableId = reader.ReadVUInt32();
                var tableInfo = ((ObjectDB) _transaction.Owner).TablesInfo.FindById(tableId);
                if (tableInfo == null)
                    return;
                var tableVersion = reader.ReadVUInt32();
                var freeContentTuple = tableInfo.GetFreeContent(tableVersion);
                if (freeContentTuple.Item1 != NeedsFreeContent.No)
                {
                    freeContentTuple.Item2(_transaction, null, reader, _freeDictionaries);
                }
            }
            else
            {
                var ido = (int) (-id) - 1;
                if (!AlreadyProcessedInstance(ido))
                    _transaction.FreeContentInNativeObject(this);
            }
        }

        bool AlreadyProcessedInstance(int ido)
        {
            if (_seenObjects == null) _seenObjects = new List<bool>();
            while (_seenObjects.Count <= ido) _seenObjects.Add(false);
            var res = _seenObjects[ido];
            _seenObjects[ido] = true;
            return res;
        }
    }

    class SimpleModificationCounter : IRelationModificationCounter
    {
        public int ModificationCounter => 0;

        public void CheckModifiedDuringEnum(int prevModification)
        {
        }

        public void MarkModification()
        {
        }
    }
}
