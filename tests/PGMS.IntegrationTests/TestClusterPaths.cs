using System.IO;

/// <summary>Files of the isolated test cluster; Test-Integration.ps1 sets one data directory per PostgreSQL major.</summary>
internal static class TestClusterPaths
{
    public static string Data(string file) =>
        Path.GetFullPath(Path.Combine(Environment.GetEnvironmentVariable("PGMS_TEST_POSTGRES_DATA") is { Length: > 0 } dir ? dir : "artifacts/postgres-test/data", file));
}
