﻿using DOL.GS.PacketHandler;
using DOL.GS.Scripts;
using DOL.Language;

namespace DOL.GS
{
    public class StagECSGameEffect : ECSGameAbilityEffect
    {
        public StagECSGameEffect(ECSGameEffectInitParams initParams, int level)
            : base(initParams)
        {
            m_level = level;
            EffectType = eEffect.Stag;
            EffectService.RequestStartEffect(this);
        }

        /// <summary>
        /// The amount of max health gained
        /// </summary>
        protected int m_amount;

        protected ushort m_originalModel;

        protected int m_level;

        public override ushort Icon
        { get { return 480; } }

        public override string Name
        {
            get
            {
                if (OwnerPlayer != null)
                    return LanguageMgr.GetTranslation(((GamePlayer)Owner).Client, "Effects.StagEffect.Name");
                else
                    return "Stag";
            }
        }

        public override bool HasPositiveEffect
        { get { return true; } }

        public override void OnStartEffect()
        {
            m_originalModel = Owner.Model;

            if (Owner != null)
            {
                if (Owner.Race == (int)eRace.Lurikeen)
                    Owner.Model = 859;
                else
                    Owner.Model = 583;
            }

            double m_amountPercent = (m_level + 0.5 + Util.RandomDouble()) / 10; //+-5% random

            if (OwnerPlayer != null)
                m_amount = (int)(OwnerPlayer.CalculateMaxHealth(OwnerPlayer.Level, OwnerPlayer.GetModified(eProperty.Constitution)) * m_amountPercent);
            else if (Owner is MimicNPC mimicOwner)
                m_amount = (int)(mimicOwner.CalculateMaxHealth(mimicOwner.Level, mimicOwner.GetModified(eProperty.Constitution)) * m_amountPercent);
            else
                m_amount = (int)(Owner.MaxHealth * m_amountPercent);

            Owner.BaseBuffBonusCategory[(int)eProperty.MaxHealth] += m_amount;
            Owner.Health += (int)(Owner.GetModified(eProperty.MaxHealth) * m_amountPercent);

            if (Owner.Health > Owner.MaxHealth)
                Owner.Health = Owner.MaxHealth;

            Owner.Emote(eEmote.StagFrenzy);

            if (OwnerPlayer != null)
            {
                OwnerPlayer.Out.SendUpdatePlayer();
                OwnerPlayer.Out.SendMessage(LanguageMgr.GetTranslation(OwnerPlayer.Client, "Effects.StagEffect.HuntsSpiritChannel"), eChatType.CT_YouHit, eChatLoc.CL_SystemWindow);
            }
        }

        public override void OnStopEffect()
        {
            Owner.Model = m_originalModel;

            Owner.BaseBuffBonusCategory[(int)eProperty.MaxHealth] -= m_amount;
            if (Owner.IsAlive && Owner.Health > Owner.MaxHealth)
                Owner.Health = Owner.MaxHealth;

            if (OwnerPlayer != null)
            {
                OwnerPlayer.Out.SendUpdatePlayer();
                // there is no animation on end of the effect
                OwnerPlayer.Out.SendMessage(LanguageMgr.GetTranslation(OwnerPlayer.Client, "Effects.StagEffect.YourHuntsSpiritEnds"), eChatType.CT_YouHit, eChatLoc.CL_SystemWindow);
            }
        }
    }
}