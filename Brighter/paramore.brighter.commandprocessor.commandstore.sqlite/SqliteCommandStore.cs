﻿// ***********************************************************************
// Assembly         : paramore.brighter.commandprocessor.commandstore.mssql
// Author           : francesco.pighi
// Created          : 06-03-2015
//
// Last Modified By : ian cooper
// Last Modified On : 06-04-2015
// ***********************************************************************
// <copyright file="MsSqlCommandStore.cs" company="">
//     Copyright (c) . All rights reserved.
// </copyright>
// <summary></summary>
// ***********************************************************************

#region Licence

/* The MIT License (MIT)
Copyright © 2014 Francesco Pighi <francesco.pighi@gmail.com>

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the “Software”), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in
all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED “AS IS”, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
THE SOFTWARE. */

#endregion

using System;
using System.Data;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using paramore.brighter.commandprocessor.commandstore.sqlite.Logging;

namespace paramore.brighter.commandprocessor.commandstore.sqlite
{
    /// <summary>
    ///     Class SqliteCommandStore.
    /// </summary>
    public class SqliteCommandStore : IAmACommandStore, IAmACommandStoreAsync
    {
        private static readonly Lazy<ILog> _logger = new Lazy<ILog>(LogProvider.For<SqliteCommandStore>);

        private const int SqliteDuplicateKeyError = 1555;
        private const int SqliteUniqueKeyError = 19;

        /// <summary>
        ///     Initializes a new instance of the <see cref="SqliteCommandStore" /> class.
        /// </summary>
        /// <param name="configuration">The configuration.</param>
        public SqliteCommandStore(SqliteCommandStoreConfiguration configuration)
        {
            Configuration = configuration;
            ContinueOnCapturedContext = false;
        }

        public void Add<T>(T command, int timeoutInMilliseconds = -1) where T : class, IRequest
        {
            var parameters = InitAddDbParameters(command);

            using (var connection = GetConnection())
            {
                connection.Open();
                var sqlAdd = GetAddSql();
                using (var sqlcmd = connection.CreateCommand())
                {
                    FormatAddCommand(timeoutInMilliseconds, parameters, sqlcmd, sqlAdd);
                    try
                    {
                        sqlcmd.ExecuteNonQuery();
                    }
                    catch (SqliteException sqliteException)
                    {
                        if (IsExceptionUnqiueOrDuplicateIssue(sqliteException))
                        {
                            _logger.Value.WarnFormat(
                                "MsSqlMessageStore: A duplicate Command with the CommandId {0} was inserted into the Message Store, ignoring and continuing",
                                command.Id);
                        }
                    }
                }
            }
        }

        private static bool IsExceptionUnqiueOrDuplicateIssue(SqliteException sqlException)
        {
            return sqlException.SqliteErrorCode == SqliteDuplicateKeyError ||
                   sqlException.SqliteErrorCode == SqliteUniqueKeyError;
        }

        public T Get<T>(Guid id, int timeoutInMilliseconds = -1) where T : class, IRequest, new()
        {
            var sql = string.Format("select * from {0} where CommandId = @CommandId", this.MessageStoreTableName);
            var parameters = new[]
            {
                this.CreateSqlParameter("CommandId", id)
            };

            return ExecuteCommand(command => ReadCommand<T>(command.ExecuteReader()), sql, timeoutInMilliseconds, parameters);
        }

        public async Task AddAsync<T>(T command, int timeoutInMilliseconds = -1, CancellationToken cancellationToken = default(CancellationToken)) where T : class, IRequest
        {
            var parameters = InitAddDbParameters(command);

            using (var connection = GetConnection())
            {
                await connection.OpenAsync(cancellationToken).ConfigureAwait(ContinueOnCapturedContext);
                var sqlAdd = GetAddSql();
                using (var sqlcmd = connection.CreateCommand())
                {
                    FormatAddCommand(timeoutInMilliseconds, parameters, sqlcmd, sqlAdd);
                    try
                    {
                        await sqlcmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(ContinueOnCapturedContext);
                    }
                    catch (SqliteException sqliteException)
                    {
                        if (!IsExceptionUnqiueOrDuplicateIssue(sqliteException)) throw;
                        _logger.Value.WarnFormat(
                            "MsSqlMessageStore: A duplicate Command with the CommandId {0} was inserted into the Message Store, ignoring and continuing",
                            command.Id);
                    }
                }
            }
        }

        public async Task<T> GetAsync<T>(Guid id, int timeoutInMilliseconds = -1, CancellationToken cancellationToken = default(CancellationToken)) where T : class, IRequest, new()
        {
            var sql = string.Format("select * from {0} where CommandId = @CommandId", MessageStoreTableName);
            var parameters = new[]
            {
                CreateSqlParameter("@CommandId", id)
            };

            return await ExecuteCommandAsync(
                async command => ReadCommand<T>(await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(ContinueOnCapturedContext)),
                sql,
                timeoutInMilliseconds,
                parameters,
                cancellationToken)
                .ConfigureAwait(ContinueOnCapturedContext);
        }

        /// <summary>
        ///     If false we the default thread synchronization context to run any continuation, if true we re-use the original
        ///     synchronization context.
        ///     Default to false unless you know that you need true, as you risk deadlocks with the originating thread if you Wait
        ///     or access the Result or otherwise block. You may need the orginating synchronization context if you need to access
        ///     thread specific storage
        ///     such as HTTPContext
        /// </summary>
        public bool ContinueOnCapturedContext { get; set; }

        public SqliteCommandStoreConfiguration Configuration { get; }

        public string MessageStoreTableName => Configuration.MessageStoreTableName;

        public DbParameter CreateSqlParameter(string parameterName, object value)
        {
            return new SqliteParameter(parameterName, value);
        }

        public DbConnection GetConnection()
        {
            return new SqliteConnection(Configuration.ConnectionString);
        }
        
        public T ExecuteCommand<T>(Func<DbCommand, T> execute, string sql, 
            int timeoutInMilliseconds, params DbParameter[] parameters)
        {
            using (var connection = GetConnection())
            using (var command = connection.CreateCommand())
            {
                if (timeoutInMilliseconds != -1) command.CommandTimeout = timeoutInMilliseconds;
                command.CommandText = sql;
                AddParamtersParamArrayToCollection(parameters, command);

                connection.Open();
                var item = execute(command);
                return item;
            }
        }

        public async Task<T> ExecuteCommandAsync<T>(Func<DbCommand, Task<T>> execute, 
            string sql, int timeoutInMilliseconds, DbParameter[] parameters, CancellationToken cancellationToken = default(CancellationToken))
        {
            using (var connection = GetConnection())
            using (var command = connection.CreateCommand())
            {
                if (timeoutInMilliseconds != -1) command.CommandTimeout = timeoutInMilliseconds;
                command.CommandText = sql;
                AddParamtersParamArrayToCollection(parameters, command);

                await connection.OpenAsync(cancellationToken).ConfigureAwait(ContinueOnCapturedContext);
                return await execute(command).ConfigureAwait(ContinueOnCapturedContext);
            }
        }

        private void FormatAddCommand(int timeoutInMilliseconds, DbParameter[] parameters, DbCommand sqlcmd, string sqlAdd)
        {
            if (timeoutInMilliseconds != -1) sqlcmd.CommandTimeout = timeoutInMilliseconds;

            sqlcmd.CommandText = sqlAdd;
            AddParamtersParamArrayToCollection(parameters, sqlcmd);
        }

        private string GetAddSql()
        {
            var sqlAdd = string.Format(
                "insert into {0} (CommandID, CommandType, CommandBody, Timestamp) values (@CommandID, @CommandType, @CommandBody, @Timestamp)",
                MessageStoreTableName);
            return sqlAdd;
        }

        public DbParameter[] InitAddDbParameters<T>(T command) where T : class, IRequest
        {
            var commandJson = JsonConvert.SerializeObject(command);
            var parameters = new[]
            {
                CreateSqlParameter("CommandID", command.Id), //was CommandId
                CreateSqlParameter("CommandType", typeof (T).Name), 
                CreateSqlParameter("CommandBody", commandJson), 
                CreateSqlParameter("Timestamp", DateTime.UtcNow)
            };
            return parameters;
        }

        public TResult ReadCommand<TResult>(IDataReader dr) where TResult : class, IRequest, new()
        {
            if (dr.Read())
            {
                var body = dr.GetString(dr.GetOrdinal("CommandBody"));
                return JsonConvert.DeserializeObject<TResult>(body);
            }

            return new TResult { Id = Guid.Empty };
        }

        public void AddParamtersParamArrayToCollection(DbParameter[] parameters, DbCommand command)
        {
            //command.Parameters.AddRange(parameters); used to work... but can't with current Sqlite lib. Iterator issue
            for (var index = 0; index < parameters.Length; index++)
            {
                command.Parameters.Add(parameters[index]);
            }
        }
    }
}