using System;
using DOL.GS.PacketHandler;

namespace DOL.GS.Commands
{
    //BRENT ADDED
    [CmdAttribute(
        "&xpmod",
        ePrivLevel.GM,
        "Change global XP modifier (no parameter to set to 1.5)",
        "/xpmod [newModifier]")]
    public class XPModCommandHandler : AbstractCommandHandler, ICommandHandler
    {
        public void OnCommand(GameClient client, string[] args)
        {
            GamePlayer player = client.Player;

            if (args.Length == 1)
            {
                ServerProperties.Properties.XP_RATE = 1.5;
                DisplayMessage(player, "Global XP modifier is now " + ServerProperties.Properties.XP_RATE);
                return;
            }

            double modifier;

            if (double.TryParse(args[1], out modifier))
            {
                ServerProperties.Properties.XP_RATE = modifier;
                DisplayMessage(player, "Global XP modifier is now " + ServerProperties.Properties.XP_RATE);
            }
            else
            {
                DisplaySyntax(client);
            }
        }
    }
}