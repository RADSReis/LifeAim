using LifeAim.Modules.Agents.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace LifeAim.Api.Controllers;

/// <summary>Operations on a LifeAim user (the id from communication.channel_users).</summary>
[ApiController]
[Route("users")]
public sealed class UsersController(IConversationReset conversationReset, IWebHostEnvironment environment) : ControllerBase
{
    /// <summary>
    /// Starts the user's conversation with Pippin over (stage, name, draft and messages; goals stay).
    /// Development only: the API has no login yet, so in production this route does not exist (404).
    /// </summary>
    [HttpDelete("{userId:guid}/conversation")]
    public async Task<IActionResult> ResetConversation(Guid userId, CancellationToken cancellationToken)
    {
        if (!environment.IsDevelopment())
        {
            return NotFound();
        }

        return await conversationReset.ResetAsync(userId, cancellationToken) ? NoContent() : NotFound();
    }
}
