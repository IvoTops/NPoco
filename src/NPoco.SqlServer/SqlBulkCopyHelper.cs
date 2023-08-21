using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using NPoco.Internal;

namespace NPoco.SqlServer
{
    public class SqlBulkCopyHelper
    {
        public static Func<DbConnection, SqlConnection> SqlConnectionResolver = dbConn => (SqlConnection)dbConn;
        public static Func<DbTransaction, SqlTransaction> SqlTransactionResolver = dbTran => (SqlTransaction)dbTran;

        public static void BulkInsert<T>(IDatabase db, IEnumerable<T> list, InsertBulkOptions? insertBulkOptions)
        {
            BulkInsert(db, list, SqlBulkCopyOptions.Default, insertBulkOptions);
        }

        public static void BulkInsert<T>(IDatabase db, IEnumerable<T> list, SqlBulkCopyOptions sqlBulkCopyOptions, InsertBulkOptions? insertBulkOptions)
        {
            using (var bulkCopy = new SqlBulkCopy(SqlConnectionResolver(db.Connection), sqlBulkCopyOptions, SqlTransactionResolver(db.Transaction)))
            {
                var table = BuildBulkInsertDataTable(db, list, bulkCopy, sqlBulkCopyOptions, insertBulkOptions);
                bulkCopy.WriteToServer(table);
            }
        }

        public static Task BulkInsertAsync<T>(IDatabase db, IEnumerable<T> list, InsertBulkOptions sqlBulkCopyOptions)
        {
            return BulkInsertAsync(db, list, SqlBulkCopyOptions.Default, sqlBulkCopyOptions);
        }

        public static async Task BulkInsertAsync<T>(IDatabase db, IEnumerable<T> list, SqlBulkCopyOptions sqlBulkCopyOptions, InsertBulkOptions insertBulkOptions)
        {
            using (var bulkCopy = new SqlBulkCopy(SqlConnectionResolver(db.Connection), sqlBulkCopyOptions, SqlTransactionResolver(db.Transaction)))
            {
                var table = BuildBulkInsertDataTable(db, list, bulkCopy, sqlBulkCopyOptions, insertBulkOptions);
                await bulkCopy.WriteToServerAsync(table).ConfigureAwait(false);
            }
        }


        private static DataTable BuildBulkInsertDataTableOld<T>(IDatabase db, IEnumerable<T> list, SqlBulkCopy bulkCopy, SqlBulkCopyOptions sqlBulkCopyOptions, InsertBulkOptions? insertBulkOptions)
        {
            var pocoData = db.PocoDataFactory.ForType(typeof(T));

            bulkCopy.BatchSize = 4096;
            bulkCopy.DestinationTableName = db.DatabaseType.EscapeTableName(pocoData.TableInfo.TableName);

            if (insertBulkOptions?.BulkCopyTimeout != null)
                bulkCopy.BulkCopyTimeout = insertBulkOptions.BulkCopyTimeout.Value;

            var table = new DataTable();
            var cols = pocoData.Columns.Where(x =>
            {
                if (x.Value.ResultColumn) return false;
                if (x.Value.ComputedColumn) return false;
                if (x.Value.ColumnName.Equals(pocoData.TableInfo.PrimaryKey, StringComparison.OrdinalIgnoreCase))
                {
                    if (sqlBulkCopyOptions == SqlBulkCopyOptions.KeepIdentity)
                        return true;

                    return pocoData.TableInfo.AutoIncrement == false;
                }
                return true;
            }).ToList();

            foreach (var col in cols)
            {
                bulkCopy.ColumnMappings.Add(col.Value.MemberInfoKey, col.Value.ColumnName);
                table.Columns.Add(col.Value.MemberInfoKey, Nullable.GetUnderlyingType(col.Value.MemberInfoData.MemberType) ?? col.Value.MemberInfoData.MemberType);
            }

            foreach (var item in list)
            {
                var values = new object[cols.Count];
                for (var i = 0; i < values.Length; i++)
                {
                    var value = db.DatabaseType.MapParameterValue(db.ProcessMapper(cols[i].Value, cols[i].Value.GetValue(item!)));
                    if (value.GetTheType() == typeof(SqlParameter))
                    {
                        value = ((SqlParameter)value).Value;
                    }

                    var newType = value.GetTheType();
                    if (newType != null && newType != typeof(DBNull))
                    {
                        table.Columns[i].DataType = newType;
                    }

                    values[i] = value;
                }

                table.Rows.Add(values);
            }
            return table;
        }


        // TOPFIX - WAY FASTER
        private static IDataReader BuildBulkInsertDataTable<T>(IDatabase db, IEnumerable<T> list, SqlBulkCopy bulkCopy, SqlBulkCopyOptions sqlBulkCopyOptions, InsertBulkOptions? insertBulkOptions)
        {
            var pocoData = db.PocoDataFactory.ForType(list.FirstOrDefault()?.GetType() ?? typeof(T));

            bulkCopy.DestinationTableName = db.DatabaseType.EscapeTableName(pocoData.TableInfo.TableName);

            if (insertBulkOptions?.BulkCopyBatchSize != null)
                bulkCopy.BatchSize = insertBulkOptions.BulkCopyBatchSize.Value;
            else bulkCopy.BatchSize = 4096;

            if (insertBulkOptions?.BulkCopyTimeout != null)
                bulkCopy.BulkCopyTimeout = insertBulkOptions.BulkCopyTimeout.Value;

            bulkCopy.EnableStreaming = insertBulkOptions?.BulkCopyStreaming ?? false;

            var table = new DataTable();
            var cols = pocoData.Columns.Where(x =>
            {
                if (x.Value.ResultColumn) return false;
                if (x.Value.ComputedColumn) return false;
                if (x.Value.ColumnName.Equals(pocoData.TableInfo.PrimaryKey, StringComparison.OrdinalIgnoreCase))
                {
                    if (sqlBulkCopyOptions == SqlBulkCopyOptions.KeepIdentity) return true;
                    return pocoData.TableInfo.AutoIncrement == false;
                }
                return true;
            }).ToList();

            foreach (var col in cols)
                bulkCopy.ColumnMappings.Add(col.Value.MemberInfoKey, col.Value.ColumnName);

            var dr = new DataReader<T>(db, list, cols);
            return dr;
        }

        public class DataReader<T> : IDataReader
        {
            private readonly IEnumerator<T> _data;
            private readonly IDatabase db;
            private readonly List<KeyValuePair<string, PocoColumn>> cols;

            private T? _rowdata;

            public DataReader(IDatabase Db, IEnumerable<T> Pocos, List<KeyValuePair<string, PocoColumn>> Cols)
            {
                db = Db;
                _data = Pocos.GetEnumerator();
                cols = Cols;
            }

            public int FieldCount => cols.Count;

            public bool Read()
            {
                if (!_data.MoveNext()) return false;
                //
                _rowdata = _data.Current;
                //
                return true;
            }

            public object GetValue(int i)
            {
                var value = db.DatabaseType.MapParameterValue(db.ProcessMapper(cols[i].Value, cols[i].Value.GetValue(_rowdata!)));
                if (value.GetTheType() == typeof(SqlParameter)) value = ((SqlParameter)value).Value;
                return value;
            }

            public int GetOrdinal(string name)
            {
                for (var x = 0; x < cols.Count; x++) if (cols[x].Value.ColumnName.Equals(name, StringComparison.OrdinalIgnoreCase)) return x;
                return -1;
            }

            public bool IsDBNull(int i) { return GetValue(i) == null; }

            public int GetValues(object[] values) { throw new NotImplementedException(); }

            public void Dispose() { throw new NotImplementedException(); }

            public string GetName(int i) { throw new NotImplementedException(); }

            public string GetDataTypeName(int i) { throw new NotImplementedException(); }

            public Type GetFieldType(int i) { throw new NotImplementedException(); }

            public bool GetBoolean(int i) { throw new NotImplementedException(); }

            public byte GetByte(int i) { throw new NotImplementedException(); }

            public long GetBytes(int i, long fieldOffset, byte[]? buffer, int bufferoffset, int length) { throw new NotImplementedException(); }

            public char GetChar(int i) { throw new NotImplementedException(); }

            public long GetChars(int i, long fieldoffset, char[]? buffer, int bufferoffset, int length) { throw new NotImplementedException(); }

            public Guid GetGuid(int i) { throw new NotImplementedException(); }

            public short GetInt16(int i) { throw new NotImplementedException(); }

            public int GetInt32(int i) { throw new NotImplementedException(); }

            public long GetInt64(int i) { throw new NotImplementedException(); }

            public float GetFloat(int i) { throw new NotImplementedException(); }

            public double GetDouble(int i) { throw new NotImplementedException(); }

            public string GetString(int i) { throw new NotImplementedException(); }

            public decimal GetDecimal(int i) { throw new NotImplementedException(); }

            public DateTime GetDateTime(int i) { throw new NotImplementedException(); }

            public IDataReader GetData(int i) { throw new NotImplementedException(); }

            object IDataRecord.this[int i] { get { throw new NotImplementedException(); } }

            object IDataRecord.this[string name] { get { throw new NotImplementedException(); } }

            public void Close() { throw new NotImplementedException(); }

            public DataTable GetSchemaTable() { throw new NotImplementedException(); }

            public bool NextResult() { throw new NotImplementedException(); }

            public int Depth { get; private set; }
            public bool IsClosed { get; private set; }
            public int RecordsAffected { get; private set; }
        }
    }

}
