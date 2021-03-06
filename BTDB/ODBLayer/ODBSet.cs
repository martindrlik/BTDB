using System;
using System.Collections;
using System.Collections.Generic;
using BTDB.Buffer;
using BTDB.FieldHandler;
using BTDB.KVDBLayer;
using BTDB.StreamLayer;

namespace BTDB.ODBLayer
{
    public class ODBSet<TKey> : IOrderedSet<TKey>, IQuerySizeDictionary<TKey>
    {
        readonly IInternalObjectDBTransaction _tr;
        readonly IFieldHandler _keyHandler;
        readonly Func<AbstractBufferedReader, IReaderCtx, TKey> _keyReader;
        readonly Action<TKey, AbstractBufferedWriter, IWriterCtx> _keyWriter;
        readonly IKeyValueDBTransaction _keyValueTr;
        readonly KeyValueDBTransactionProtector _keyValueTrProtector;
        readonly ulong _id;
        readonly byte[] _prefix;
        int _count;
        int _modificationCounter;

        public ODBSet(IInternalObjectDBTransaction tr, ODBDictionaryConfiguration config, ulong id)
        {
            _tr = tr;
            _keyHandler = config.KeyHandler;
            _id = id;
            var o = ObjectDB.AllDictionariesPrefix.Length;
            _prefix = new byte[o + PackUnpack.LengthVUInt(_id)];
            Array.Copy(ObjectDB.AllDictionariesPrefix, _prefix, o);
            PackUnpack.PackVUInt(_prefix, ref o, _id);
            _keyReader = ((Func<AbstractBufferedReader, IReaderCtx, TKey>)config.KeyReader)!;
            _keyWriter = ((Action<TKey, AbstractBufferedWriter, IWriterCtx>)config.KeyWriter)!;
            _keyValueTr = _tr.KeyValueDBTransaction;
            _keyValueTrProtector = _tr.TransactionProtector;
            _count = -1;
        }

        // ReSharper disable once UnusedMember.Global
        public ODBSet(IInternalObjectDBTransaction tr, ODBDictionaryConfiguration config) : this(tr, config, tr.AllocateDictionaryId()) { }

        static void ThrowModifiedDuringEnum()
        {
            throw new InvalidOperationException("DB modified during iteration");
        }

        // ReSharper disable once UnusedMember.Global
        public static void DoSave(IWriterCtx ctx, IOrderedSet<TKey>? dictionary, int cfgId)
        {
            var writerCtx = (IDBWriterCtx)ctx;
            if (!(dictionary is ODBSet<TKey> goodDict))
            {
                var tr = writerCtx.GetTransaction();
                var id = tr.AllocateDictionaryId();
                goodDict = new ODBSet<TKey>(tr, (ODBDictionaryConfiguration)writerCtx.FindInstance(cfgId), id);
                if (dictionary != null)
                    foreach (var pair in dictionary)
                        goodDict.Add(pair);
            }
            ctx.Writer().WriteVUInt64(goodDict._id);
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        void ICollection<TKey>.Add(TKey item)
        {
            Add(item!);
        }

        public void ExceptWith(IEnumerable<TKey> other)
        {
            foreach (var key in other)
            {
                Remove(key);
            }
        }

        public void IntersectWith(IEnumerable<TKey> other)
        {
            throw new NotSupportedException();
        }

        public bool IsProperSubsetOf(IEnumerable<TKey> other)
        {
            throw new NotSupportedException();
        }

        public bool IsProperSupersetOf(IEnumerable<TKey> other)
        {
            throw new NotSupportedException();
        }

        public bool IsSubsetOf(IEnumerable<TKey> other)
        {
            throw new NotSupportedException();
        }

        public bool IsSupersetOf(IEnumerable<TKey> other)
        {
            throw new NotSupportedException();
        }

        public bool Overlaps(IEnumerable<TKey> other)
        {
            throw new NotSupportedException();
        }

        public bool SetEquals(IEnumerable<TKey> other)
        {
            throw new NotSupportedException();
        }

        public void SymmetricExceptWith(IEnumerable<TKey> other)
        {
            throw new NotSupportedException();
        }

        public void UnionWith(IEnumerable<TKey> other)
        {
            foreach (var key in other)
            {
                Add(key);
            }
        }

        public void Clear()
        {
            _keyValueTrProtector.Start();
            _modificationCounter++;
            _keyValueTr.SetKeyPrefix(_prefix);
            _keyValueTr.EraseAll();
            _count = 0;
        }

        public bool Contains(TKey item)
        {
            var keyBytes = KeyToByteArray(item);
            _keyValueTrProtector.Start();
            _keyValueTr.SetKeyPrefix(_prefix);
            return _keyValueTr.Find(keyBytes) == FindResult.Exact;
        }

        public void CopyTo(TKey[] array, int arrayIndex)
        {
            if (array == null) throw new ArgumentNullException(nameof(array));
            if ((arrayIndex < 0) || (arrayIndex > array.Length))
            {
                throw new ArgumentOutOfRangeException(nameof(arrayIndex), arrayIndex, "Needs to be nonnegative ");
            }
            if ((array.Length - arrayIndex) < Count)
            {
                throw new ArgumentException("Array too small");
            }
            foreach (var item in this)
            {
                array[arrayIndex++] = item;
            }
        }

        public int Count
        {
            get
            {
                if (_count == -1)
                {
                    _keyValueTrProtector.Start();
                    _keyValueTr.SetKeyPrefix(_prefix);
                    _count = (int)Math.Min(_keyValueTr.GetKeyValueCount(), int.MaxValue);
                }
                return _count;
            }
        }

        public bool IsReadOnly => false;

        ByteBuffer KeyToByteArray(TKey key)
        {
            var writer = new ByteBufferWriter();
            IWriterCtx ctx = null;
            if (_keyHandler.NeedsCtx()) ctx = new DBWriterCtx(_tr, writer);
            _keyWriter(key, writer, ctx);
            return writer.Data;
        }

        TKey ByteArrayToKey(ByteBuffer data)
        {
            var reader = new ByteBufferReader(data);
            IReaderCtx ctx = null;
            if (_keyHandler.NeedsCtx()) ctx = new DBReaderCtx(_tr, reader);
            return _keyReader(reader, ctx);
        }

        public bool Add(TKey key)
        {
            var keyBytes = KeyToByteArray(key);
            _keyValueTrProtector.Start();
            _modificationCounter++;
            _keyValueTr.SetKeyPrefix(_prefix);
            var created = _keyValueTr.CreateOrUpdateKeyValue(keyBytes, ByteBuffer.NewEmpty());
            if (created) NotifyAdded();
            return created;
        }

        public bool Remove(TKey key)
        {
            var keyBytes = KeyToByteArray(key);
            _keyValueTrProtector.Start();
            _modificationCounter++;
            _keyValueTr.SetKeyPrefix(_prefix);
            if (_keyValueTr.Find(keyBytes) != FindResult.Exact) return false;
            _keyValueTr.EraseCurrent();
            NotifyRemoved();
            return true;
        }

        void NotifyAdded()
        {
            if (_count != -1)
            {
                if (_count != int.MaxValue) _count++;
            }
        }

        void NotifyRemoved()
        {
            if (_count != -1)
            {
                if (_count == int.MaxValue)
                {
                    _count = (int)Math.Min(_keyValueTr.GetKeyValueCount(), int.MaxValue);
                }
                else
                {
                    _count--;
                }
            }
        }

        public IEnumerator<TKey> GetEnumerator()
        {
            long prevProtectionCounter = 0;
            var prevModificationCounter = 0;
            long pos = 0;
            while (true)
            {
                _keyValueTrProtector.Start();
                if (pos == 0)
                {
                    prevModificationCounter = _modificationCounter;
                    _keyValueTr.SetKeyPrefix(_prefix);
                    if (!_keyValueTr.FindFirstKey()) break;
                }
                else
                {
                    if (_keyValueTrProtector.WasInterupted(prevProtectionCounter))
                    {
                        if (prevModificationCounter != _modificationCounter)
                            ThrowModifiedDuringEnum();
                        _keyValueTr.SetKeyPrefix(_prefix);
                        if (!_keyValueTr.SetKeyIndex(pos)) break;
                    }
                    else
                    {
                        if (!_keyValueTr.FindNextKey()) break;
                    }
                }
                prevProtectionCounter = _keyValueTrProtector.ProtectionCounter;
                var keyBytes = _keyValueTr.GetKey();
                var key = ByteArrayToKey(keyBytes);
                yield return key;
                pos++;
            }
        }

        public IEnumerable<TKey> GetReverseEnumerator()
        {
            long prevProtectionCounter = 0;
            var prevModificationCounter = 0;
            var pos = long.MaxValue;
            while (true)
            {
                _keyValueTrProtector.Start();
                if (pos == long.MaxValue)
                {
                    prevModificationCounter = _modificationCounter;
                    _keyValueTr.SetKeyPrefix(_prefix);
                    if (!_keyValueTr.FindLastKey()) break;
                    pos = _keyValueTr.GetKeyIndex();
                }
                else
                {
                    if (_keyValueTrProtector.WasInterupted(prevProtectionCounter))
                    {
                        if (prevModificationCounter != _modificationCounter)
                            ThrowModifiedDuringEnum();
                        _keyValueTr.SetKeyPrefix(_prefix);
                        if (!_keyValueTr.SetKeyIndex(pos)) break;
                    }
                    else
                    {
                        if (!_keyValueTr.FindPreviousKey()) break;
                    }
                }
                prevProtectionCounter = _keyValueTrProtector.ProtectionCounter;
                var keyBytes = _keyValueTr.GetKey();
                var key = ByteArrayToKey(keyBytes);
                yield return key;
                pos--;
            }
        }

        public IEnumerable<TKey> GetIncreasingEnumerator(TKey start)
        {
            var startKeyBytes = KeyToByteArray(start);
            long prevProtectionCounter = 0;
            var prevModificationCounter = 0;
            long pos = 0;
            while (true)
            {
                _keyValueTrProtector.Start();
                if (pos == 0)
                {
                    prevModificationCounter = _modificationCounter;
                    _keyValueTr.SetKeyPrefix(_prefix);
                    bool startOk;
                    switch (_keyValueTr.Find(startKeyBytes))
                    {
                        case FindResult.Exact:
                        case FindResult.Next:
                            startOk = true;
                            break;
                        case FindResult.Previous:
                            startOk = _keyValueTr.FindNextKey();
                            break;
                        case FindResult.NotFound:
                            startOk = false;
                            break;
                        default:
                            throw new ArgumentOutOfRangeException();
                    }
                    if (!startOk) break;
                    pos = _keyValueTr.GetKeyIndex();
                }
                else
                {
                    if (_keyValueTrProtector.WasInterupted(prevProtectionCounter))
                    {
                        if (prevModificationCounter != _modificationCounter)
                            ThrowModifiedDuringEnum();
                        _keyValueTr.SetKeyPrefix(_prefix);
                        if (!_keyValueTr.SetKeyIndex(pos)) break;
                    }
                    else
                    {
                        if (!_keyValueTr.FindNextKey()) break;
                    }
                }
                prevProtectionCounter = _keyValueTrProtector.ProtectionCounter;
                var keyBytes = _keyValueTr.GetKey();
                var key = ByteArrayToKey(keyBytes);
                yield return key;
                pos++;
            }
        }

        public IEnumerable<TKey> GetDecreasingEnumerator(TKey start)
        {
            var startKeyBytes = KeyToByteArray(start);
            long prevProtectionCounter = 0;
            var prevModificationCounter = 0;
            var pos = long.MaxValue;
            while (true)
            {
                _keyValueTrProtector.Start();
                if (pos == long.MaxValue)
                {
                    prevModificationCounter = _modificationCounter;
                    _keyValueTr.SetKeyPrefix(_prefix);
                    bool startOk;
                    switch (_keyValueTr.Find(startKeyBytes))
                    {
                        case FindResult.Exact:
                        case FindResult.Previous:
                            startOk = true;
                            break;
                        case FindResult.Next:
                            startOk = _keyValueTr.FindPreviousKey();
                            break;
                        case FindResult.NotFound:
                            startOk = false;
                            break;
                        default:
                            throw new ArgumentOutOfRangeException();
                    }
                    if (!startOk) break;
                    pos = _keyValueTr.GetKeyIndex();
                }
                else
                {
                    if (_keyValueTrProtector.WasInterupted(prevProtectionCounter))
                    {
                        if (prevModificationCounter != _modificationCounter)
                            ThrowModifiedDuringEnum();
                        _keyValueTr.SetKeyPrefix(_prefix);
                        if (!_keyValueTr.SetKeyIndex(pos)) break;
                    }
                    else
                    {
                        if (!_keyValueTr.FindPreviousKey()) break;
                    }
                }
                prevProtectionCounter = _keyValueTrProtector.ProtectionCounter;
                var keyBytes = _keyValueTr.GetKey();
                var key = ByteArrayToKey(keyBytes);
                yield return key;
                pos--;
            }
        }

        public long RemoveRange(AdvancedEnumeratorParam<TKey> param)
        {
            _keyValueTrProtector.Start();
            _modificationCounter++;
            _keyValueTr.SetKeyPrefix(_prefix);
            long startIndex;
            long endIndex;
            if (param.EndProposition == KeyProposition.Ignored)
            {
                endIndex = _keyValueTr.GetKeyValueCount() - 1;
            }
            else
            {
                var keyBytes = KeyToByteArray(param.End);
                switch (_keyValueTr.Find(keyBytes))
                {
                    case FindResult.Exact:
                        endIndex = _keyValueTr.GetKeyIndex();
                        if (param.EndProposition == KeyProposition.Excluded)
                        {
                            endIndex--;
                        }
                        break;
                    case FindResult.Previous:
                        endIndex = _keyValueTr.GetKeyIndex();
                        break;
                    case FindResult.Next:
                        endIndex = _keyValueTr.GetKeyIndex() - 1;
                        break;
                    case FindResult.NotFound:
                        endIndex = -1;
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }
            if (param.StartProposition == KeyProposition.Ignored)
            {
                startIndex = 0;
            }
            else
            {
                var keyBytes = KeyToByteArray(param.Start);
                switch (_keyValueTr.Find(keyBytes))
                {
                    case FindResult.Exact:
                        startIndex = _keyValueTr.GetKeyIndex();
                        if (param.StartProposition == KeyProposition.Excluded)
                        {
                            startIndex++;
                        }
                        break;
                    case FindResult.Previous:
                        startIndex = _keyValueTr.GetKeyIndex() + 1;
                        break;
                    case FindResult.Next:
                        startIndex = _keyValueTr.GetKeyIndex();
                        break;
                    case FindResult.NotFound:
                        startIndex = 0;
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }
            _keyValueTr.EraseRange(startIndex, endIndex);
            _count = -1;
            return Math.Max(0, endIndex - startIndex + 1);
        }

        public IEnumerable<KeyValuePair<uint, uint>> QuerySizeEnumerator()
        {
            long prevProtectionCounter = 0;
            var prevModificationCounter = 0;
            long pos = 0;
            while (true)
            {
                _keyValueTrProtector.Start();
                if (pos == 0)
                {
                    prevModificationCounter = _modificationCounter;
                    _keyValueTr.SetKeyPrefix(_prefix);
                    if (!_keyValueTr.FindFirstKey()) break;
                }
                else
                {
                    if (_keyValueTrProtector.WasInterupted(prevProtectionCounter))
                    {
                        if (prevModificationCounter != _modificationCounter)
                            ThrowModifiedDuringEnum();
                        _keyValueTr.SetKeyPrefix(_prefix);
                        if (!_keyValueTr.SetKeyIndex(pos)) break;
                    }
                    else
                    {
                        if (!_keyValueTr.FindNextKey()) break;
                    }
                }
                prevProtectionCounter = _keyValueTrProtector.ProtectionCounter;
                var size = _keyValueTr.GetStorageSizeOfCurrentKey();
                yield return size;
                pos++;
            }
        }

        public KeyValuePair<uint, uint> QuerySizeByKey(TKey key)
        {
            var keyBytes = KeyToByteArray(key);
            _keyValueTrProtector.Start();
            _keyValueTr.SetKeyPrefix(_prefix);
            var found = _keyValueTr.Find(keyBytes) == FindResult.Exact;
            if (!found)
            {
                throw new ArgumentException("Key not found in Set");
            }
            var size = _keyValueTr.GetStorageSizeOfCurrentKey();
            return size;
        }

        class AdvancedEnumerator : IEnumerable<TKey>, IEnumerator<TKey>
        {
            readonly ODBSet<TKey> _owner;
            readonly KeyValueDBTransactionProtector _keyValueTrProtector;
            readonly IKeyValueDBTransaction _keyValueTr;
            long _prevProtectionCounter;
            readonly int _prevModificationCounter;
            readonly uint _startPos;
            readonly uint _count;
            uint _pos;
            SeekState _seekState;
            readonly bool _ascending;

            public AdvancedEnumerator(ODBSet<TKey> owner, AdvancedEnumeratorParam<TKey> param)
            {
                _owner = owner;
                _keyValueTrProtector = _owner._keyValueTrProtector;
                _keyValueTr = _owner._keyValueTr;
                _ascending = param.Order == EnumerationOrder.Ascending;
                _keyValueTrProtector.Start();
                _prevModificationCounter = _owner._modificationCounter;
                _prevProtectionCounter = _keyValueTrProtector.ProtectionCounter;
                _keyValueTr.SetKeyPrefix(_owner._prefix);
                long startIndex;
                long endIndex;
                if (param.EndProposition == KeyProposition.Ignored)
                {
                    endIndex = _keyValueTr.GetKeyValueCount() - 1;
                }
                else
                {
                    var keyBytes = _owner.KeyToByteArray(param.End);
                    switch (_keyValueTr.Find(keyBytes))
                    {
                        case FindResult.Exact:
                            endIndex = _keyValueTr.GetKeyIndex();
                            if (param.EndProposition == KeyProposition.Excluded)
                            {
                                endIndex--;
                            }
                            break;
                        case FindResult.Previous:
                            endIndex = _keyValueTr.GetKeyIndex();
                            break;
                        case FindResult.Next:
                            endIndex = _keyValueTr.GetKeyIndex() - 1;
                            break;
                        case FindResult.NotFound:
                            endIndex = -1;
                            break;
                        default:
                            throw new ArgumentOutOfRangeException();
                    }
                }
                if (param.StartProposition == KeyProposition.Ignored)
                {
                    startIndex = 0;
                }
                else
                {
                    var keyBytes = _owner.KeyToByteArray(param.Start);
                    switch (_keyValueTr.Find(keyBytes))
                    {
                        case FindResult.Exact:
                            startIndex = _keyValueTr.GetKeyIndex();
                            if (param.StartProposition == KeyProposition.Excluded)
                            {
                                startIndex++;
                            }
                            break;
                        case FindResult.Previous:
                            startIndex = _keyValueTr.GetKeyIndex() + 1;
                            break;
                        case FindResult.Next:
                            startIndex = _keyValueTr.GetKeyIndex();
                            break;
                        case FindResult.NotFound:
                            startIndex = 0;
                            break;
                        default:
                            throw new ArgumentOutOfRangeException();
                    }
                }
                _count = (uint)Math.Max(0, endIndex - startIndex + 1);
                _startPos = (uint)(_ascending ? startIndex : endIndex);
                _pos = 0;
                _seekState = SeekState.Undefined;
            }

            void Seek()
            {
                if (_ascending)
                    _keyValueTr.SetKeyIndex(_startPos + _pos);
                else
                    _keyValueTr.SetKeyIndex(_startPos - _pos);
                _seekState = SeekState.Ready;
            }

            public IEnumerator<TKey> GetEnumerator()
            {
                return this;
            }

            IEnumerator IEnumerable.GetEnumerator()
            {
                return this;
            }

            public bool MoveNext()
            {
                if (_seekState == SeekState.Ready)
                    _pos++;
                if (_pos >= _count)
                {
                    Current = default;
                    return false;
                }
                _keyValueTrProtector.Start();
                if (_keyValueTrProtector.WasInterupted(_prevProtectionCounter))
                {
                    if (_prevModificationCounter != _owner._modificationCounter)
                        ThrowModifiedDuringEnum();
                    _keyValueTr.SetKeyPrefix(_owner._prefix);
                    Seek();
                }
                else if (_seekState != SeekState.Ready)
                {
                    Seek();
                }
                else
                {
                    if (_ascending)
                    {
                        _keyValueTr.FindNextKey();
                    }
                    else
                    {
                        _keyValueTr.FindPreviousKey();
                    }
                }
                _prevProtectionCounter = _keyValueTrProtector.ProtectionCounter;
                Current = _owner.ByteArrayToKey(_keyValueTr.GetKey());
                return true;
            }

            public void Reset()
            {
                throw new NotSupportedException();
            }

            public TKey Current { get; private set; }

            object? IEnumerator.Current => Current;

            public void Dispose()
            {
            }
        }

        public IEnumerable<TKey> GetAdvancedEnumerator(AdvancedEnumeratorParam<TKey> param)
        {
            return new AdvancedEnumerator(this, param);
        }
    }
}
