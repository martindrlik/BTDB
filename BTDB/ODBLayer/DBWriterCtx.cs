using System.Collections.Generic;
using BTDB.Encrypted;
using BTDB.StreamLayer;

namespace BTDB.ODBLayer
{
    public class DBWriterCtx : IDBWriterCtx
    {
        readonly IInternalObjectDBTransaction _transaction;
        readonly AbstractBufferedWriter _writer;
        Dictionary<object, int>? _objectIdMap;
        int _lastId;

        public DBWriterCtx(IInternalObjectDBTransaction transaction, AbstractBufferedWriter writer)
        {
            _transaction = transaction;
            _writer = writer;
        }

        public bool WriteObject(object @object)
        {
            return CommonWriteObject(@object, false, true);
        }

        bool CommonWriteObject(object? @object, bool autoRegister, bool forceInline)
        {
            if (@object == null)
            {
                _writer.WriteVInt64(0);
                return false;
            }
            var oid = _transaction.StoreIfNotInlined(@object, autoRegister, forceInline);
            if (oid != ulong.MaxValue)
            {
                _writer.WriteVInt64((long)oid);
                return false;
            }
            if (_objectIdMap == null) _objectIdMap = new Dictionary<object, int>();
            if (_objectIdMap.TryGetValue(@object, out var cid))
            {
                _writer.WriteVInt64(-cid);
                return false;
            }
            _lastId++;
            _objectIdMap.Add(@object, _lastId);
            _writer.WriteVInt64(-_lastId);
            return true;
        }

        public void WriteNativeObject(object @object)
        {
            if (!CommonWriteObject(@object, true, true)) return;
            _transaction.WriteInlineObject(@object, this);
        }

        public void WriteNativeObjectPreventInline(object @object)
        {
            if (!CommonWriteObject(@object, true, false)) return;
            _transaction.WriteInlineObject(@object, this);
        }

        public AbstractBufferedWriter Writer()
        {
            return _writer;
        }

        public void WriteEncryptedString(EncryptedString value)
        {
            var writer = new ByteBufferWriter();
            writer.WriteString(value);
            var cipher = _transaction.Owner.GetSymmetricCipher();
            var plain = writer.Data.AsSyncReadOnlySpan();
            var encSize = cipher.CalcEncryptedSizeFor(plain);
            var enc = new byte[encSize];
            cipher.Encrypt(plain, enc);
            _writer.WriteByteArray(enc);
        }

        public void WriteOrderedEncryptedString(EncryptedString value)
        {
            var writer = new ByteBufferWriter();
            writer.WriteString(value);
            var cipher = _transaction.Owner.GetSymmetricCipher();
            var plain = writer.Data.AsSyncReadOnlySpan();
            var encSize = cipher.CalcOrderedEncryptedSizeFor(plain);
            var enc = new byte[encSize];
            cipher.OrderedEncrypt(plain, enc);
            _writer.WriteByteArray(enc);
        }

        public int RegisterInstance(object content)
        {
            return ((IInstanceRegistry)_transaction.Owner).RegisterInstance(content);
        }

        public object FindInstance(int id)
        {
            return ((IInstanceRegistry)_transaction.Owner).FindInstance(id);
        }

        public IInternalObjectDBTransaction GetTransaction()
        {
            return _transaction;
        }
    }
}
