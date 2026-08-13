namespace Cuckoo.Core;

/// <summary>Base exception class for this application.</summary>
public class MinerException : Exception
{
    public MinerException() : base("Unknown miner error") { }
    public MinerException(string message) : base(message) { }
    public MinerException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>Control-flow signal: the application was requested to exit.</summary>
public sealed class ExitRequestException : MinerException
{
    public ExitRequestException() : base("Application was requested to exit") { }
}

/// <summary>Control-flow signal: the application was requested to reload entirely.</summary>
public sealed class ReloadRequestException : MinerException
{
    public ReloadRequestException() : base("Application was requested to reload entirely") { }
}

/// <summary>A web request did not return what we wanted it to.</summary>
public class RequestException : MinerException
{
    public RequestException() : base("Unknown error during request") { }
    public RequestException(string message) : base(message) { }
    public RequestException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>A request became invalid inside its retry loop.</summary>
public sealed class RequestInvalidException : RequestException
{
    public RequestInvalidException() : base("Request became invalid during its retry loop") { }
}

/// <summary>The websocket connection has been closed.</summary>
public sealed class WebsocketClosedException : RequestException
{
    /// <summary>True if the closing was caused by our side receiving a close frame.</summary>
    public bool Received { get; }

    public WebsocketClosedException(bool received = false) : base("Websocket has been closed")
        => Received = received;
}

/// <summary>An error occurred during the login phase.</summary>
public class LoginException : RequestException
{
    public LoginException() : base("Unknown error during login") { }
    public LoginException(string message) : base(message) { }
}

/// <summary>The most dreaded thing about automated scripts...</summary>
public sealed class CaptchaRequiredException : LoginException
{
    public CaptchaRequiredException() : base("Captcha is required") { }
}

/// <summary>A GQL request returned an error response.</summary>
public sealed class GqlException : RequestException
{
    public GqlException(string message) : base(message) { }
}
