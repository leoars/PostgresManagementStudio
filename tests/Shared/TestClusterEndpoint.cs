using System.Globalization;

internal static class TestClusterEndpoint
{
    internal const int DefaultPort = 55439;

    internal static int Port => ParsePort(Environment.GetEnvironmentVariable("PGMS_TEST_PORT"));

    internal static int ParsePort(string? value)
    {
        if (value is null) return DefaultPort;
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int port)
            || port is < 1024 or > 65535)
            throw new InvalidOperationException("PGMS_TEST_PORT must be an integer from 1024 to 65535 for the isolated loopback cluster.");
        return port;
    }
}
