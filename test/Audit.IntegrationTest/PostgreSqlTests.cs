﻿#if NET461 || NETCOREAPP2_0 || NETCOREAPP3_0 || NET5_0_OR_GREATER
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using Audit.Core;
using Audit.PostgreSql.Providers;

namespace Audit.IntegrationTest
{
    [TestFixture]
    [Category("PostgreSQL")]
    public class PostgreSqlTests
    {
        [OneTimeSetUp]
        public void Init()
        {
            SetupConfiguredPostgreSqlDataProvider();
            AuditScope.Log("test", null);
        }
        
        [Test]
        public void Test_PostgreDataProvider_CustomDataColumn()
        {
            var overrideEventType = Guid.NewGuid().ToString();
            var dp = GetConfiguredPostgreSqlDataProvider(overrideEventType);
            
            var scope = AuditScope.Create("test", null);
            scope.Dispose();

            var id = scope.EventId;

            var getEvent = dp?.GetEvent<AuditEvent>(id);

            Assert.AreEqual(overrideEventType, getEvent?.EventType);
        }

        [Test]
        public void Test_PostgreDataProvider_Paging_No_Where()
        {
            const int pageNumberOne = 1;
            const int pageSize = 10;

            var dp = SetupConfiguredPostgreSqlDataProvider();
            
            IEnumerable<AuditEvent> events = dp?.EnumerateEvents(pageNumberOne, pageSize);
            ICollection<AuditEvent> realizedEvents = events.ToList();
            Assert.IsNotNull(realizedEvents);
        }
        
        [Test]
        public void Test_PostgreDataProvider_Paging_OutOfRange_Inputs()
        {
            /* define negative values.  the code should "safety-ize" them to page-number=1 and page-size=1 */
            const int pageNumberOne = -333;
            const int pageSize = -444;

            var dp = SetupConfiguredPostgreSqlDataProvider();

            IEnumerable<AuditEvent> events = dp?.EnumerateEvents(pageNumberOne, pageSize);
            ICollection<AuditEvent> realizedEvents = events.ToList();
            Assert.IsNotNull(realizedEvents);
        }        
        
        [Test]
        public void Test_PostgreDataProvider_Paging_With_Where()
        {
            const int pageNumber = 3;
            const int pageSize = 10;

            string whereExpression = @""""+ GetLastUpdatedColumnNameColumnName() + @""" > '12/31/1900'";
            var dp = SetupConfiguredPostgreSqlDataProvider();

            IEnumerable<AuditEvent> events = dp?.EnumerateEvents(pageNumber, pageSize, whereExpression);
            ICollection<AuditEvent> realizedEvents = events.ToList();
            Assert.IsNotNull(realizedEvents);
        }
        
        [Test]
        public void Test_EnumerateEvents_WhereExpression()
        {
            var overrideEventType = Guid.NewGuid().ToString();
            var dp = GetConfiguredPostgreSqlDataProvider(overrideEventType);

            var scope = AuditScope.Create("test", null);
            scope.Dispose();

            var whereExpression = @"""" + GetLastUpdatedColumnNameColumnName() + @""" > '12/31/1900'";
            var events = dp?.EnumerateEvents(whereExpression);
            var ev = events?.FirstOrDefault();

            Assert.IsNotNull(ev);
        }

        [Test]
        public void Test_EnumerateEvents_WhereSortByExpression()
        {
            var overrideEventType = Guid.NewGuid().ToString();
            var dp = GetConfiguredPostgreSqlDataProvider(overrideEventType);

            var scope = AuditScope.Create("test", null);
            scope.Dispose();

            var whereExpression = @"""" + GetLastUpdatedColumnNameColumnName() + @""" > '12/31/1900'";
            var events = dp?.EnumerateEvents<AuditEvent>(whereExpression, GetLastUpdatedColumnNameColumnName(), "1");
            var ev = events?.FirstOrDefault();

            Assert.IsNotNull(ev);
        }
        
        private static string GetLastUpdatedColumnNameColumnName()
        {
            /* this value is encapsulated so both RunLocalAuditConfiguration and Test_PostgreDataProvider_Paging_With_Where use the same value */
            return "updated_date";
        }

        private static CustomPostgreSqlDataProvider SetupConfiguredPostgreSqlDataProvider()
        {
            string overrideEventType = Guid.NewGuid().ToString();
            return GetConfiguredPostgreSqlDataProvider(overrideEventType);
        }
        
        private static CustomPostgreSqlDataProvider GetConfiguredPostgreSqlDataProvider(string overrideEventType)
        {
            Audit.Core.Configuration.Setup()
                .UseCustomProvider(new CustomPostgreSqlDataProvider(config => config
                    .ConnectionString(AzureSettings.PostgreSqlConnectionString)
                    .TableName("event")
                    .IdColumnName(_ => "id")
                    .DataColumn("data", Audit.PostgreSql.Configuration.DataType.JSONB, auditEvent =>
                    {
                        auditEvent.EventType = overrideEventType;
                        return auditEvent.ToJson();
                    })
                    .LastUpdatedColumnName(GetLastUpdatedColumnNameColumnName())
                    .CustomColumn("event_type", ev => ev.EventType)
                    .CustomColumn("some_date", ev => DateTime.UtcNow)
                    .CustomColumn("some_null", ev => null)))
                .WithCreationPolicy(EventCreationPolicy.InsertOnEnd)
                .ResetActions();
            
            var dp = (CustomPostgreSqlDataProvider)Configuration.DataProvider;
            return dp;
        }
    }

    /// <summary>
    /// Custom postgre sql data provider with pagination
    /// </summary>
    public class CustomPostgreSqlDataProvider : PostgreSqlDataProvider
    {
        public CustomPostgreSqlDataProvider(Action<PostgreSql.Configuration.IPostgreSqlProviderConfigurator> config) : base(config)
        {
        }

        public IEnumerable<AuditEvent> EnumerateEvents(int pageNumber, int pageSize, string whereExpression = null)
        {
            return EnumerateEvents<AuditEvent>(pageNumber, pageSize, whereExpression);
        }

        public IEnumerable<T> EnumerateEvents<T>(int pageNumber, int pageSize, string whereExpression = null) where T : AuditEvent
        {
            int safePageNumber = Math.Max(pageNumber, 1);
            int safePageSize = Math.Max(pageSize, 1);
            int offset = (safePageSize * safePageNumber) - safePageSize;
            string schema = GetSchema(null);

            /* note, the "where-clause" must be applied "inside" the "derived1" inner-query...thus the variable name "inner-where-clause" */
            string innerWhereClause = string.IsNullOrWhiteSpace(whereExpression) ? "" : $" WHERE {whereExpression}";

            /* create the paging-query...which uses an inner-derived-table to capture the ROW_NUMBER values */
            string fullPagingSql =
                $@"SELECT aet.""{GetDataColumnName(null)}"" FROM {schema}""{GetTableName(null)}"" as aet"
                +
                $@" JOIN ( SELECT ""{GetIdColumnName(null)}"", ROW_NUMBER() OVER (ORDER BY ""{GetIdColumnName(null)}"")"
                +
                $@" as ""RowNumb"" FROM {schema}""{GetTableName(null)}"" innerAet"
                +
                $@" {innerWhereClause}) as derived1"
                +
                $@" ON derived1.""{GetIdColumnName(null)}"" = aet.""{GetIdColumnName(null)}"" WHERE derived1.""RowNumb"" > {offset}"
                +
                $@" ORDER BY aet.""{GetIdColumnName(null)}"" ASC LIMIT {safePageSize};";

            return EnumerateEventsByFullSql<T>(fullPagingSql);
        }
    }
}
#endif