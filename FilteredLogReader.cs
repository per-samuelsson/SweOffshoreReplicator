﻿using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Starcounter;
using Starcounter.TransactionLog;

namespace LogStreamer
{
    /// <summary>
    /// FilteredLogReader reads the transaction log looking for transactions 
    /// with commit ID's that modify a set of tables with commit ID's less
    /// than the transaction commit ID, or all transactions if there is no
    /// dictionary. Then makes the next transaction available, with any
    /// non-matching table operations removed from the transaction.
    /// 
    /// It also does loop filtering based on peer database GUID. All
    /// transactions that contain an insert or update of
    /// LogStreamer.State where DatabaseGuid equals the peer
    /// will be ignored in entirety.
    /// 
    /// Finally, no operations on LogStreamer tables will be sent.
    /// </summary>
    class FilteredLogReader
    {
        private readonly LogStreamerSession _logStreamer;
        private Dictionary<string, ulong> _tablePos = null;
        private readonly HashSet<string> _tableFilter;
        private readonly ILogReader _reader;

        public FilteredLogReader(LogStreamerSession logStreamer, Dictionary<string, ulong> tablePos, HashSet<string> tableFilter)
        {
            _logStreamer = logStreamer;

            // make a copy of the table position dictionary, stripping out prefix
            if (tablePos != null && tablePos.Count > 0)
            {
                _tablePos = new Dictionary<string, ulong>(tablePos.Count);
                foreach (var kv in tablePos)
                {
                    _tablePos.Add(StripDatabasePrefix(kv.Key), kv.Value);
                }
            }

            // make a copy of the table filter set, stripping out prefix
            if (tableFilter != null)
            {
                _tableFilter = new HashSet<string>();
                foreach (var tableNameOrId in tableFilter)
                {
                    var tableName = StripDatabasePrefix(tableNameOrId);
                    if (tableName != string.Empty)
                    {
                        _tableFilter.Add(tableName);
                    }
                }
            }

            _reader = _logStreamer.LogManager.OpenLog(Starcounter.Internal.StarcounterEnvironment.DatabaseNameLower, LogStreamerParent.TransactionLogDirectory, new LogPosition { commit_id = FindStartCommitId() });
        }

        public string StripDatabasePrefix(string tableNameOrId)
        {
            return _logStreamer.StripDatabasePrefix(tableNameOrId);
        }

        public string PeerGuidString
        {
            get
            {
                return _logStreamer.PeerGuidString;
            }
        }

        public string PeerTableIdPrefix
        {
            get
            {
                return _logStreamer.PeerTableIdPrefix;
            }
        }

        private ulong FindStartCommitId()
        {
            ulong databaseCommitId = 0;
            ulong minTableCommitId = 0;
            if (_tablePos != null)
            {
                minTableCommitId = ulong.MaxValue;
                foreach (KeyValuePair<string, ulong> kv in _tablePos)
                {
                    if (kv.Key == "")
                    {
                        databaseCommitId = kv.Value;
                    }
                    else if (kv.Value < minTableCommitId)
                    {
                        minTableCommitId = kv.Value;
                    }
                }
                if (_tablePos.Remove(string.Empty))
                {
                    if (_tablePos.Count == 0)
                    {
                        _tablePos = null;
                    }
                }
            }
 
            // if there is no filter or no table positions, use database commit id
            if (_tableFilter == null || _tablePos == null)
            {
                return databaseCommitId;
            }

            // if one of the tables in the filter is not known, use database commit id
            foreach (var tableName in _tableFilter)
            {
                if (!_tablePos.ContainsKey(tableName))
                {
                    return databaseCommitId;
                }
            }

            // if the database commit id has progressed further than lowest table, use that
            if (minTableCommitId < databaseCommitId)
                return databaseCommitId;

            // we have a filter and all tables named in it are known
            return minTableCommitId;
        }

        private bool FilterLoops(column_update[] columns)
        {
            for (int i = 0; i < columns.Length; i++)
            {
                if (columns[i].name == "TableId")
                {
                    if (((string)columns[i].value).StartsWith(PeerTableIdPrefix))
                        return true;
                }
            }
            return false;
        }

        private bool FilterTable(string tableName, ulong commitId)
        {
            if (_tableFilter != null && !_tableFilter.Contains(tableName))
            {
                return true;
            }
            if (_tablePos != null)
            {
                ulong minId;
                if (_tablePos.TryGetValue(tableName, out minId))
                {
                    if (commitId <= minId)
                    {
                        // receiver has already seen that transaction for this table
                        return true;
                    }
                    _tablePos.Remove(tableName);
                    if (_tablePos.Count == 0)
                    {
                        _tablePos = null;
                    }
                }
            }
            return false;
        }

        private bool FilterTransaction(LogReadResult lrr)
        {
            var tran = lrr.transaction_data;
            var commitId = lrr.continuation_position.commit_id;
            int index;

            index = 0;
            while (index < tran.creates.Count)
            {
                var record = tran.creates[index];
                if (record.table.StartsWith("LogStreamer."))
                {
                    if (record.table == "LogStreamer.LastPosition" && FilterLoops(record.columns))
                    {
                        return true;
                    }
                    tran.creates.RemoveAt(index);
                    continue;
                }
                if (FilterTable(record.table, commitId))
                {
                    tran.creates.RemoveAt(index);
                    continue;
                }
                index++;
            }

            index = 0;
            while (index < tran.updates.Count)
            {
                var record = tran.updates[index];
                if (record.table.StartsWith("LogStreamer."))
                {
                    if (record.table == "LogStreamer.LastPosition" && FilterLoops(record.columns))
                    {
                        return true;
                    }
                    tran.updates.RemoveAt(index);
                    continue;
                }
                if (FilterTable(record.table, commitId))
                {
                    tran.updates.RemoveAt(index);
                    continue;
                }
                index++;
            }

            index = 0;
            while (index < tran.deletes.Count)
            {
                var record = tran.deletes[index];
                if (record.table.StartsWith("LogStreamer."))
                {
                    tran.deletes.RemoveAt(index);
                    continue;
                }
                if (FilterTable(record.table, commitId))
                {
                    tran.deletes.RemoveAt(index);
                    continue;
                }
                index++;
            }

            if (tran.updates.Count == 0 && tran.creates.Count == 0 && tran.deletes.Count == 0)
            {
                // transaction is empty
                return true;
            }

            return false;
        }

        public async Task<LogReadResult> ReadAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                LogReadResult lrr = await _reader.ReadAsync(ct);
                if (lrr != null && !FilterTransaction(lrr))
                    return lrr;
            }
            return null;
        }
    }
}
