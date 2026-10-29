using Microsoft.AspNetCore.Mvc;
using Noelia.Infrastructure.Security.ServiceTokens;

namespace Noelia.Infrastructure.Tests.Security.ServiceTokens;

/// <summary>A controller door: the attribute is the only thing that makes it one.</summary>
[ApiController]
[Route("internal-mvc")]
public sealed class MvcDoorController : ControllerBase
{
    [HttpGet("ping")]
    [ServiceCaller("advisor")]
    public IActionResult Ping() => Content("pong");

    [HttpPost("note")]
    [ServiceCaller("notification")]
    public IActionResult Note([FromBody] NoteDto body) => Content("note " + body.Text);
}

public sealed record NoteDto(string Text);
