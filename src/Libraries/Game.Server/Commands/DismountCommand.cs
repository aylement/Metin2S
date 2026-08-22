using QuantumCore.API.Game;

namespace QuantumCore.Game.Commands;

[Command("dismount", "Gets you off your mount")]
public class DismountCommand : ICommandHandler
{
    public Task ExecuteAsync(CommandContext context)
    {
        context.Player.Unmount();
        context.Player.SendChatInfo("Dismounted");
        return Task.CompletedTask;
    }
}
