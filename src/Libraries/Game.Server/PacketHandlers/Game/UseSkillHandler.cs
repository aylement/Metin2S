using Microsoft.Extensions.Logging;
using QuantumCore.API;
using QuantumCore.API.Game.Types.Skills;
using QuantumCore.API.Packets;
using QuantumCore.API.PluginTypes;

namespace QuantumCore.Game.PacketHandlers.Game;

public class UseSkillHandler : IGamePacketHandler<PlayerUseSkill>
{
    private readonly ILogger<UseSkillHandler> _logger;

    public UseSkillHandler(ILogger<UseSkillHandler> logger)
    {
        _logger = logger;
    }

    public Task ExecuteAsync(GamePacketContext<PlayerUseSkill> ctx, CancellationToken token = default)
    {
        var player = ctx.Connection.Player;
        if (player is null)
        {
            ctx.Connection.Close();
            return Task.CompletedTask;
        }

        var skillId = (ESkill)ctx.Packet.SkillId;
        if (!Enum.IsDefined(skillId))
        {
            _logger.LogWarning("Received PlayerUseSkill for unknown skill id {SkillId}", ctx.Packet.SkillId);
            return Task.CompletedTask;
        }

        player.Skills.Use(skillId, (uint)ctx.Packet.TargetVid);
        return Task.CompletedTask;
    }
}
