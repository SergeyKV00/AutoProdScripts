using AutoProdScripts.Core.Sql;

namespace AutoProdScripts.Core.Tests;

public class ReadOnlyValidatorTests
{
    [Theory]
    [InlineData("SELECT * FROM dbo.Orders WHERE Id = @id")]
    [InlineData("WITH c AS (SELECT 1 AS a) SELECT * FROM c")]
    [InlineData("USE Common_test; SELECT 1")]
    [InlineData("SELECT * FROM dbo.Orders WITH (NOLOCK)")]
    [InlineData("SELECT Id INTO #t FROM dbo.Orders")]
    [InlineData("CREATE TABLE #t (Id int); INSERT INTO #t SELECT Id FROM dbo.Orders; UPDATE #t SET Id = 1; DELETE #t; TRUNCATE TABLE #t; DROP TABLE #t")]
    [InlineData("CREATE TABLE #t (Id int); CREATE INDEX ix ON #t(Id); ALTER TABLE #t ADD Name nvarchar(10)")]
    [InlineData("DECLARE @t TABLE (Id int); INSERT @t VALUES (1); UPDATE @t SET Id = 2; DELETE FROM @t")]
    [InlineData("UPDATE x SET Id = 1 FROM #t AS x JOIN dbo.Orders o ON o.Id = x.Id")]
    [InlineData("DELETE x FROM @t x WHERE x.Id = 1")]
    [InlineData("MERGE #t AS t USING dbo.Orders AS s ON t.Id = s.Id WHEN MATCHED THEN UPDATE SET Id = s.Id;")]
    [InlineData("SET NOCOUNT ON; SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED; DECLARE @a int; SET @a = 1; SELECT @a")]
    [InlineData("IF 1 = 1 BEGIN SELECT 1 END ELSE SELECT 2")]
    [InlineData("BEGIN TRY SELECT 1 END TRY BEGIN CATCH THROW; END CATCH")]
    [InlineData("SELECT 'update dbo.Orders; exec x' AS note -- delete from dbo.Orders")]
    [InlineData("SELECT 1\nGO\nSELECT 2")]
    public void Allows(string sql)
    {
        var analysis = ReadOnlyValidator.Analyze(sql);

        Assert.True(analysis.IsAllowed, string.Join("; ", analysis.Violations));
    }

    [Theory]
    [InlineData("UPDATE dbo.Orders SET Id = 1")]
    [InlineData("DELETE FROM dbo.Orders")]
    [InlineData("INSERT INTO dbo.Orders VALUES (1)")]
    [InlineData("MERGE dbo.Orders AS t USING #s AS s ON t.Id = s.Id WHEN MATCHED THEN DELETE;")]
    [InlineData("UPDATE o SET Id = 1 FROM dbo.Orders o")]
    [InlineData("UPDATE o SET Id = 1 FROM #t x JOIN dbo.Orders o ON o.Id = x.Id")]
    [InlineData("UPDATE [#t] SET Id = 1 FROM dbo.Orders AS [#t]")]
    [InlineData("DELETE [@t] FROM dbo.Orders AS [@t]")]
    [InlineData("WITH c AS (SELECT * FROM dbo.Orders) UPDATE c SET Id = 1")]
    [InlineData("WITH [#c] AS (SELECT * FROM dbo.Orders) DELETE [#c]")]
    [InlineData("UPDATE q SET Id = 1 FROM (SELECT * FROM dbo.Orders) q")]
    [InlineData("SELECT * INTO dbo.Copy FROM dbo.Orders")]
    [InlineData("SELECT * INTO ##global FROM dbo.Orders")]
    [InlineData("SELECT * INTO Other.dbo.[#t] FROM dbo.Orders")]
    [InlineData("INSERT INTO #t OUTPUT inserted.Id INTO dbo.Audit SELECT 1")]
    [InlineData("INSERT INTO #t SELECT * FROM (DELETE FROM dbo.Orders OUTPUT deleted.Id) d")]
    [InlineData("INSERT INTO #t EXEC dbo.DoWork")]
    [InlineData("EXEC dbo.DoWork")]
    [InlineData("sp_who")]
    [InlineData("EXEC ('DELETE FROM dbo.Orders')")]
    [InlineData("EXEC sp_executesql N'SELECT 1'")]
    [InlineData("EXECUTE AS USER = 'x'")]
    [InlineData("IF 1 = 1 BEGIN DELETE FROM dbo.Orders END")]
    [InlineData("BEGIN TRY SELECT 1 END TRY BEGIN CATCH DELETE FROM dbo.Orders END CATCH")]
    [InlineData("SELECT 1\nGO\nDROP TABLE dbo.Orders")]
    [InlineData("CREATE TABLE dbo.X (Id int)")]
    [InlineData("DROP TABLE #t, dbo.Orders")]
    [InlineData("TRUNCATE TABLE dbo.Orders")]
    [InlineData("ALTER TABLE dbo.Orders ADD X int")]
    [InlineData("CREATE INDEX ix ON dbo.Orders(Id)")]
    [InlineData("CREATE PROCEDURE #p AS SELECT 1")]
    [InlineData("CREATE VIEW dbo.V AS SELECT 1 AS a")]
    [InlineData("COMMIT")]
    [InlineData("ROLLBACK")]
    [InlineData("BEGIN TRAN")]
    [InlineData("GRANT SELECT ON dbo.Orders TO public")]
    [InlineData("KILL 55")]
    [InlineData("SHUTDOWN")]
    [InlineData("DBCC FREEPROCCACHE")]
    [InlineData("BACKUP DATABASE x TO DISK = 'x'")]
    [InlineData("WAITFOR DELAY '00:01'")]
    [InlineData("SELECT NEXT VALUE FOR dbo.Seq")]
    [InlineData("SELECT * FROM OPENQUERY(srv, 'DELETE FROM x')")]
    [InlineData("SELECT * FROM OPENROWSET('SQLNCLI', 'x';'u';'p', 'SELECT 1')")]
    [InlineData("SELECT * FROM dbo.Orders WITH (TABLOCKX)")]
    [InlineData("SELECT * FROM dbo.Orders WITH (UPDLOCK, HOLDLOCK)")]
    [InlineData("SET TRANSACTION ISOLATION LEVEL SERIALIZABLE")]
    [InlineData("DECLARE @c CURSOR; SET @c = CURSOR FOR SELECT 1")]
    [InlineData("RAISERROR('x', 10, 1) WITH LOG")]
    [InlineData("SELECT FROM WHERE")]
    [InlineData("")]
    public void Rejects(string sql)
    {
        Assert.False(ReadOnlyValidator.Analyze(sql).IsAllowed);
    }

    [Fact]
    public void FindsExternalParametersOnly()
    {
        var analysis = ReadOnlyValidator.Analyze(
            "DECLARE @local int = @id; DECLARE @t TABLE (a int); SELECT @local, @@ROWCOUNT, @Name, @name FROM @t");

        Assert.Equal(["@id", "@Name"], analysis.Parameters);
    }

    [Fact]
    public void SplitsBatchesByGo()
    {
        var analysis = ReadOnlyValidator.Analyze("SELECT @a\r\nGO\r\nSELECT 2");

        Assert.Equal(["SELECT @a", "SELECT 2"], analysis.Batches.Select(b => b.Text));
        Assert.Equal(["@a"], analysis.Batches[0].Parameters);
        Assert.Empty(analysis.Batches[1].Parameters);
    }
}
