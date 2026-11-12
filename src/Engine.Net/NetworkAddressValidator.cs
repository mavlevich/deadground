using System.Net;
using System.Net.Sockets;

namespace DeadGround.Engine.Net;

public static class NetworkAddressValidator
{
    public const int DefaultPort = 7777;
    public const int MinPort = 1;
    public const int MaxPort = 65535;

    public static bool TryParse(
        string? input,
        out string host,
        out int port,
        out string? errorMessage,
        int defaultPort = DefaultPort)
    {
        host = string.Empty;
        port = defaultPort;
        errorMessage = null;

        if (string.IsNullOrWhiteSpace(input))
        {
            errorMessage = "Server address cannot be empty.";
            return false;
        }

        string trimmed = input.Trim();

        // Check for raw IPv6 address or bracketed IPv6 notation
        if (IPAddress.TryParse(trimmed, out IPAddress? rawIp) && rawIp.AddressFamily == AddressFamily.InterNetworkV6)
        {
            errorMessage = "IPv6 addresses are not supported. Use IPv4 address or host name.";
            return false;
        }

        if (trimmed.StartsWith('['))
        {
            int closeBracket = trimmed.IndexOf(']');
            if (closeBracket < 0)
            {
                errorMessage = "Malformed address: missing closing bracket for IPv6.";
                return false;
            }

            errorMessage = "IPv6 addresses are not supported. Use IPv4 address or host name.";
            return false;
        }

        string hostPart;
        int colonIndex = trimmed.IndexOf(':');

        if (colonIndex >= 0)
        {
            if (trimmed.IndexOf(':', colonIndex + 1) >= 0)
            {
                errorMessage = "Invalid address format. Multiple colons found. Use host:port (e.g. 127.0.0.1:7777).";
                return false;
            }

            hostPart = trimmed[..colonIndex].Trim();
            string portPart = trimmed[(colonIndex + 1)..].Trim();

            if (string.IsNullOrEmpty(hostPart))
            {
                errorMessage = "Host name cannot be empty before colon.";
                return false;
            }

            if (string.IsNullOrEmpty(portPart))
            {
                errorMessage = "Port cannot be empty after colon.";
                return false;
            }

            if (!int.TryParse(portPart, out int parsedPort) || parsedPort < MinPort || parsedPort > MaxPort)
            {
                errorMessage = $"Port must be an integer between {MinPort} and {MaxPort}.";
                return false;
            }

            port = parsedPort;
        }
        else
        {
            hostPart = trimmed;
            port = defaultPort;
        }

        if (!IsValidHost(hostPart, out string? hostError))
        {
            errorMessage = hostError;
            return false;
        }

        host = hostPart;
        return true;
    }

    public static bool IsValidHost(string host, out string? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(host))
        {
            error = "Host name cannot be empty.";
            return false;
        }

        if (host.Length > 253)
        {
            error = "Host name is too long (maximum 253 characters).";
            return false;
        }

        if (IPAddress.TryParse(host, out IPAddress? ip))
        {
            if (ip.AddressFamily == AddressFamily.InterNetwork)
            {
                string[] segments = host.Split('.');
                if (segments.Length == 4)
                {
                    return true;
                }
            }

            error = "IPv6 addresses are not supported. Use IPv4 address or host name.";
            return false;
        }

        string[] labels = host.Split('.');
        for (int i = 0; i < labels.Length; i++)
        {
            string label = labels[i];
            if (string.IsNullOrEmpty(label))
            {
                error = "Host name cannot have empty labels (consecutive dots).";
                return false;
            }

            if (label.Length > 63)
            {
                error = "Host name label is too long (maximum 63 characters).";
                return false;
            }

            if (label.StartsWith('-') || label.EndsWith('-'))
            {
                error = "Host name labels cannot start or end with a hyphen.";
                return false;
            }

            for (int c = 0; c < label.Length; c++)
            {
                char ch = label[c];
                bool isAlphaNum = (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9');
                if (!isAlphaNum && ch != '-')
                {
                    error = $"Host name contains invalid character '{ch}'. Use letters, numbers, and hyphens.";
                    return false;
                }
            }
        }

        return true;
    }
}
