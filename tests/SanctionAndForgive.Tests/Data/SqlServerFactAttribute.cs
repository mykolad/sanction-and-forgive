namespace SanctionAndForgive.Tests.Data;

/// <summary>A test that needs a real SQL Server (bulk copy, application locks): it runs only when
/// <c>SQLSERVER_TEST_CONNECTION</c> is set (CI's clean-database-migrations job, or a local container), and is
/// skipped otherwise.</summary>
public sealed class SqlServerFactAttribute : FactAttribute
{
    public SqlServerFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SQLSERVER_TEST_CONNECTION")))
        {
            Skip = "Set SQLSERVER_TEST_CONNECTION to run tests against SQL Server.";
        }
    }
}
