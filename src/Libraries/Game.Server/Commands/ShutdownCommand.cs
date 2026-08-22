using Microsoft.Extensions.Hosting;
using QuantumCore.API.Game;

namespace QuantumCore.Game.Commands;

/// <summary>
/// Gracefully stops the server process, giving <see cref="GameServer"/>'s shutdown path a chance to
/// persist every connected player's current state (in particular position) before it exits - see
/// GameServer.ExecuteAsync's post-loop persistence block. This exists because a forceful process kill
/// (e.g. `taskkill /F`, or the OS refusing a non-forced `taskkill` on a console app with no window, which
/// was confirmed to happen for this project's dev executables) skips that path entirely, silently
/// reverting connected players to their last real login/logout position on next login.
/// </summary>
[Command("shutdown", "Gracefully stops the server, persisting all connected players first")]
public class ShutdownCommand : ICommandHandler
{
    private readonly IHostApplicationLifetime _lifetime;

    public ShutdownCommand(IHostApplicationLifetime lifetime)
    {
        _lifetime = lifetime;
    }

    public Task ExecuteAsync(CommandContext context)
    {
        context.Player.SendChatInfo("Server is shutting down gracefully...");
        _lifetime.StopApplication();
        return Task.CompletedTask;
    }
}
