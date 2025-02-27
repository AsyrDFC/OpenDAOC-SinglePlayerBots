﻿using System;
using DOL.Database;
using DOL.Events;
using DOL.GS;
using DOL.GS.Spells;
using NUnit.Framework;

namespace DOL.Tests.Unit.Gameserver
{
    [TestFixture]
    class UT_SpellHandler
    {
        [OneTimeSetUp]
        public void SetUpFakeServer()
        {
            FakeServer.Load();
        }

        #region CastSpell
        [Test]
        public void CastSpell_OnNPCTarget_True()
        {
            var caster = NewFakePlayer();
            var target = NewFakeNPC();
            var spell = NewFakeSpell();
            var spellHandler = new SpellHandler(caster, spell, null);

            bool isCastSpellSuccessful = spellHandler.StartSpell(target);

            Assert.IsTrue(isCastSpellSuccessful);
        }

        [Test]
        public void CastSpell_OnNPCTarget_CastStartingEventFired()
        {
            var caster = NewFakePlayer();
            var target = NewFakeNPC();
            var spell = NewFakeSpell();
            var spellHandler = new SpellHandler(caster, spell, null);

            spellHandler.StartSpell(target);

            var actual = caster.lastNotifiedEvent;
            var expected = GameLivingEvent.CastStarting;
            Assert.AreEqual(expected, actual);
            Assert.AreEqual((caster.lastNotifiedEventArgs as CastingEventArgs).SpellHandler, spellHandler);
        }

        [Test]
        public void CastSpell_FocusSpell_FiveEventsOnCasterAndOneEventOnTarget()
        {
            var caster = NewFakePlayer();
            var target = NewFakePlayer();
            var spell = NewFakeSpell();
            spell.fakeIsFocus = true;
            spell.fakeTarget = eSpellTarget.REALM;
            spell.Duration = 20;
            var spellHandler = new SpellHandler(caster, spell, NewSpellLine());
            var gameEventMgrSpy = GameEventMgrSpy.LoadAndReturn();
            UtilChanceIsHundredPercent.Enable();

            spellHandler.StartSpell(target);

            var eventNumberOnCaster = gameEventMgrSpy.GameObjectEventCollection[caster].Count;
            var eventNumberOnTarget = gameEventMgrSpy.GameObjectEventCollection[target].Count;
            Assert.AreEqual(5, eventNumberOnCaster, "Caster has not the right amount of event subscriptions");
            Assert.AreEqual(1, eventNumberOnTarget, "Target has not the right amount of event subscriptions");
        }

        [Test]
        public void CastSpell_FocusSpellAndCasterMoves_AllEventsRemoved()
        {
            var caster = NewFakePlayer();
            var target = NewFakeNPC();
            var spell = NewFakeSpell();
            spell.fakeIsFocus = true;
            spell.fakeTarget = eSpellTarget.ENEMY;
            spell.Duration = 20;
            var spellHandler = new SpellHandler(caster, spell, NewSpellLine());
            var gameEventMgrSpy = GameEventMgrSpy.LoadAndReturn();
            UtilChanceIsHundredPercent.Enable();

            spellHandler.StartSpell(target);
            caster.OnPlayerMove();

            var eventNumberOnCaster = gameEventMgrSpy.GameObjectEventCollection[caster].Count;
            var eventNumberOnTarget = gameEventMgrSpy.GameObjectEventCollection[target].Count;
            Assert.AreEqual(0, eventNumberOnCaster, "Caster has not the right amount of event subscriptions");
            Assert.AreEqual(0, eventNumberOnTarget, "Target has not the right amount of event subscriptions");
        }

        [Test]
        public void CastSpell_FocusSpellCastAndTicksOnce_AllEventsRemoved()
        {
            var caster = NewFakePlayer();
            var target = NewFakePlayer();
            var spell = NewFakeSpell();
            spell.fakeIsFocus = true;
            spell.fakeTarget = eSpellTarget.REALM;
            spell.Duration = 20;
            spell.fakeFrequency = 20;
            spell.fakeSpellType = eSpellType.DamageShield;
            spell.fakePulse = 1;
            var spellHandler = new SpellHandler(caster, spell, NewSpellLine());
            var gameEventMgrSpy = GameEventMgrSpy.LoadAndReturn();

            Assert.IsTrue(spellHandler.StartSpell(target));
            target.fakeRegion.FakeElapsedTime = 2;
            spellHandler.StartSpell(target); //tick
            caster.OnPlayerMove();

            var eventNumberOnCaster = gameEventMgrSpy.GameObjectEventCollection[caster].Count;
            var eventNumberOnTarget = gameEventMgrSpy.GameObjectEventCollection[target].Count;
            Assert.AreEqual(0, eventNumberOnCaster, "Caster has not the right amount of event subscriptions");
            Assert.AreEqual(0, eventNumberOnTarget, "Target has not the right amount of event subscriptions");
        }

        [Test]
        public void CheckBeginCast_NPCTarget_True()
        {
            var caster = NewFakePlayer();
            var spell = NewFakeSpell();
            var spellHandler = new SpellHandler(caster, spell, null);

            bool isBeginCastSuccessful = spellHandler.CheckBeginCast(NewFakeNPC());
            Assert.IsTrue(isBeginCastSuccessful);
        }
        #endregion CastSpell

        #region CalculateDamageVariance
        [Test]
        public void CalculateDamageVariance_TargetIsGameLiving_MinIs125Percent()
        {
            var target = NewFakeLiving();
            var spellLine = NewSpellLine();
            var spellHandler = new SpellHandler(null, null, spellLine);

            spellHandler.CalculateDamageVariance(target, out double actual, out double ignoredValue);

            Assert.AreEqual(1.25, actual);
        }

        [Test]
        public void CalculateDamageVariance_TargetIsGameLiving_MaxIs125Percent()
        {
            var target = NewFakeLiving();
            var spellLine = NewSpellLine();
            var spellHandler = new SpellHandler(null, null, spellLine);

            spellHandler.CalculateDamageVariance(target, out double ignoredValue, out double actual);

            Assert.AreEqual(1.25, actual);
        }

        [Test]
        public void CalculateDamageVariance_SpellLineIsItemEffects_MinIs100Percent()
        {
            var spellLine = new SpellLine("Item Effects", "", "", false);
            var spellHandler = new SpellHandler(null, null, spellLine);

            spellHandler.CalculateDamageVariance(null, out double actual, out double ignoredValue);

            Assert.AreEqual(1.00, actual);
        }

        [Test]
        public void CalculateDamageVariance_SpellLineIsCombatStyleEffects_MinIs100Percent()
        {
            var spellLine = new SpellLine("Combat Style Effects", "", "", false);
            var spellHandler = new SpellHandler(null, null, spellLine);

            spellHandler.CalculateDamageVariance(null, out double actual, out double ignoredValue);

            Assert.AreEqual(1.00, actual);
        }

        [Test]
        public void CalculateDamageVariance_SpellLineIsCombatStyleEffects_MaxIs150Percent()
        {
            var spellLine = new SpellLine("Combat Style Effects", "", "", false);
            var spellHandler = new SpellHandler(null, null, spellLine);

            spellHandler.CalculateDamageVariance(null, out double ignoredValue, out double actual);

            Assert.AreEqual(1.5, actual);
        }

        [Test]
        public void CalculateDamageVariance_SpellLineIsReservedSpells_MinAndMaxIs100Percent()
        {
            var spellLine = new SpellLine("Reserved Spells", "", "", false);
            var spellHandler = new SpellHandler(null, null, spellLine);

            spellHandler.CalculateDamageVariance(null, out double actualMin, out double actualMax);

            Assert.AreEqual(1.0, actualMin);
            Assert.AreEqual(1.0, actualMax);
        }

        [Test]
        public void CalculateDamageVariance_SourceAndTargetLevel30AndSpecLevel16_MinIs75Percent()
        {
            var source = new FakePlayer();
            var target = NewFakeLiving();
            source.modifiedSpecLevel = 16;
            source.Level = 30;
            target.Level = 30;
            var spellLine = NewSpellLine();
            var spellHandler = new SpellHandler(source, null, spellLine);

            spellHandler.CalculateDamageVariance(target, out double actual, out double ignoredValue);

            Assert.AreEqual(0.75, actual);
        }

        [Test]
        public void CalculateDamageVariance_SameLevelButNoSpec_MinIs25Percent()
        {
            var source = new FakePlayer();
            var target = NewFakeLiving();
            source.modifiedSpecLevel = 1;
            source.Level = 30;
            target.Level = 30;
            var spellLine = NewSpellLine();
            var spellHandler = new SpellHandler(source, null, spellLine);

            spellHandler.CalculateDamageVariance(target, out double actual, out double ignoredValue);

            Assert.AreEqual(0.25, actual);
        }

        [Test]
        public void CalculateDamageVariance_SameLevelButFiveSpecLevelOverTargetLevel_MinIs127Percent()
        {
            var source = new FakePlayer();
            var target = NewFakeLiving();
            source.modifiedSpecLevel = 35;
            source.Level = 30;
            target.Level = 30;
            var spellLine = NewSpellLine();
            var spellHandler = new SpellHandler(source, null, spellLine);

            spellHandler.CalculateDamageVariance(target, out double actual, out double ignoredValue);

            Assert.AreEqual(1.27, actual);
        }

        [Test]
        public void CalculateDamageVariance_NoSpecButSourceHasTwiceTheTargetLevel_MinIs55Percent()
        {
            var source = new FakePlayer();
            var target = NewFakeLiving();
            source.modifiedSpecLevel = 1;
            source.Level = 30;
            target.Level = 15;
            var spellLine = NewSpellLine();
            var spellHandler = new SpellHandler(source, null, spellLine);

            spellHandler.CalculateDamageVariance(target, out double actual, out double ignoredValue);

            Assert.AreEqual(0.55, actual);
        }

        [Test]
        public void CalculateDamageVariance_NoSpecButSourceHasTwiceTheTargetLevel_MaxIs155Percente()
        {
            var source = new FakePlayer();
            var target = NewFakeLiving();
            source.modifiedSpecLevel = 1;
            source.Level = 30;
            target.Level = 15;
            var spellLine = NewSpellLine();
            var spellHandler = new SpellHandler(source, null, spellLine);

            spellHandler.CalculateDamageVariance(target, out double ignoredValue, out double actual);

            Assert.AreEqual(1.55, actual);
        }
        #endregion CalculateDamageVariance

        #region CalculateDamageBase
        [Test]
        public void CalculateDamageBase_SpellDamageIs100AndCombatStyleEffect_ReturnAround72()
        {
            var spell = NewFakeSpell();
            spell.Damage = 100;
            var source = NewFakePlayer();
            var target = NewFakePlayer();
            var spellLine = new SpellLine(GlobalSpellsLines.Combat_Styles_Effect, "", "", false);
            var spellHandler = new SpellHandler(source, spell, spellLine);

            double actual = spellHandler.CalculateDamageBase(target);

            double expected = 100 * (0 + 200) / 275.0;
            Assert.AreEqual(expected, actual);
        }

        [Test]
        public void CalculateDamageBase_SpellDamageIs100SourceIsAnimistWith100Int_ReturnAround109()
        {
            var spell = NewFakeSpell();
            spell.Damage = 100;
            var source = NewFakePlayer();
            source.fakeCharacterClass = new CharacterClassAnimist();
            source.modifiedIntelligence = 100;
            var target = NewFakePlayer();
            var spellLine = NewSpellLine();
            var spellHandler = new SpellHandler(source, spell, spellLine);

            double actual = spellHandler.CalculateDamageBase(target);

            double expected = 100 * (100 + 200) / 275.0;
            Assert.AreEqual(expected, actual, 0.001);
        }

        [Test]
        public void CalculateDamageBase_SpellDamageIs100SourceIsAnimistPetWith100IntAndOwnerWith100Int_ReturnAround119()
        {
            var spell = NewFakeSpell();
            spell.Damage = 100;
            var owner = NewFakePlayer();
            owner.fakeCharacterClass = new CharacterClassAnimist();
            owner.modifiedIntelligence = 100;
            owner.Level = 50; 
            var brain = new FakeControlledBrain();
            brain.fakeOwner = owner;
            GameSummonedPet source = new GameSummonedPet(brain);
            source.Level = 50; //temporal coupling through AutoSetStat()
            source.Intelligence = 100;
            var target = NewFakePlayer();
            var spellLine = NewSpellLine();
            var spellHandler = new SpellHandler(source, spell, spellLine);

            double actual = spellHandler.CalculateDamageBase(target);

            double expected = 100 * (100 + 200) / 275.0 * (100 + 200) / 275.0;
            Assert.AreEqual(expected, actual, 0.001);
        }

        [Test]
        public void CalculateDamageBase_SpellDamageIs100FromGameNPCWithoutOwner_ReturnAround119()
        {
            GameLiving.LoadCalculators(); //temporal coupling and global state
            var spell = NewFakeSpell();
            spell.Damage = 100;
            var source = NewFakeNPC();
            source.Level = 50;
            source.Intelligence = 100;
            var target = NewFakePlayer();
            var spellLine = NewSpellLine();
            var spellHandler = new SpellHandler(source, spell, spellLine);

            double actual = spellHandler.CalculateDamageBase(target);

            double expected = 100 * (100 + 200) / 275.0;
            Assert.AreEqual(expected, actual, 0.001);
        }
        #endregion CalculateDamageBase

        #region CalculateToHitChance
        [Test]
        public void CalculateToHitChance_BaseChance_Return85()
        {
            var spell = NewFakeSpell();
            var source = NewFakePlayer();
            var target = NewFakePlayer();
            var spellLine = NewSpellLine();
            var spellHandler = new SpellHandler(source, spell, spellLine);

            int actual = spellHandler.CalculateToHitChance(target);

            Assert.AreEqual(85, actual);
        }

        [Test]
        public void CalculateToHitChance_SpellLevelIs50TargetLevelIsZero_Return110()
        {
            var spell = NewFakeSpell();
            spell.Level = 50;
            var source = NewFakePlayer();
            var target = NewFakePlayer();
            target.Level = 0;
            var spellLine = NewSpellLine();
            var spellHandler = new SpellHandler(source, spell, spellLine);

            int actual = spellHandler.CalculateToHitChance(target);

            Assert.AreEqual(110, actual);
        }

        [Test]
        public void CalculateToHitChance_SpellBonusIsTen_Return90()
        {
            var spell = NewFakeSpell();
            var source = NewFakePlayer();
            source.modifiedSpellLevel = 10; //spellBonus
            var target = NewFakePlayer();
            var spellLine = NewSpellLine();
            var spellHandler = new SpellHandler(source, spell, spellLine);

            int actual = spellHandler.CalculateToHitChance(target);

            Assert.AreEqual(90, actual);
        }

        [Test]
        public void CalculateToHitChance_SpellBonusIsSeven_Return88()
        {
            var spell = NewFakeSpell();
            var source = NewFakePlayer();
            source.modifiedSpellLevel = 7; //spellBonus
            var target = NewFakePlayer();
            var spellLine = NewSpellLine();
            var spellHandler = new SpellHandler(source, spell, spellLine);

            int actual = spellHandler.CalculateToHitChance(target);

            Assert.AreEqual(88, actual);
        }

        [Test]
        public void CalculateToHitChance_SourceSpellBonusIsTenSpellLevelAndTargetLevelAre50_Return85()
        {
            var spell = NewFakeSpell();
            spell.Level = 50;
            var source = NewFakePlayer();
            source.modifiedSpellLevel = 10; //spellBonus
            source.modifiedToHitBonus = 0;
            var target = NewFakePlayer();
            target.Level = 50;
            var spellLine = NewSpellLine();
            var spellHandler = new SpellHandler(source, spell, spellLine);

            int actual = spellHandler.CalculateToHitChance(target);

            Assert.AreEqual(85, actual);
        }

        [Test]
        public void CalculateToHitChance_SameTargetAndSpellLevelWithFiveToHitBonus_Return90()
        {
            var spell = NewFakeSpell();
            spell.Level = 50;
            var source = NewFakePlayer();
            source.modifiedSpellLevel = 0; //spellBonus
            source.modifiedToHitBonus = 5;
            var target = NewFakePlayer();
            target.Level = 50;
            var spellLine = NewSpellLine();
            var spellHandler = new SpellHandler(source, spell, spellLine);

            int actual = spellHandler.CalculateToHitChance(target);

            Assert.AreEqual(90, actual);
        }

        [Test]
        public void CalculateToHitChance_TargetIsNPCLevel50SourceIsLevel50PlayerAndSpellLevelIs40_Return80()
        {
            GS.ServerProperties.Properties.PVE_SPELL_CONHITPERCENT = 10;
            var spell = NewFakeSpell();
            spell.Level = 40;
            var source = NewFakePlayer();
            source.modifiedEffectiveLevel = 50;
            var target = NewFakeNPC();
            target.Level = 50;
            target.modifiedEffectiveLevel = 50;
            var spellLine = NewSpellLine();
            var spellHandler = new SpellHandler(source, spell, spellLine);

            int actual = spellHandler.CalculateToHitChance(target);

            Assert.AreEqual(80, actual);
        }

        #endregion

        private static GameLiving NewFakeLiving() => new FakeLiving();
        private static FakePlayerSpy NewFakePlayer() => new FakePlayerSpy() { Realm = eRealm.Albion };
        private static FakeNPC NewFakeNPC() => new FakeNPC();
        private static FakeSpell NewFakeSpell() => new FakeSpell();
        private static SpellLine NewSpellLine() => new SpellLine("", "", "", false);

        private class FakeSpell : Spell
        {
            public bool fakeIsFocus = false;
            public eSpellTarget fakeTarget = eSpellTarget.SELF;
            public int fakeFrequency = 0;
            public eSpellType fakeSpellType = 0;
            public int fakePulse = 0;
            public int fakeRange = 0;

            public FakeSpell() : base(new DbSpell(), 0) { }

            public override int Pulse => fakePulse;
            public override eSpellType SpellType => fakeSpellType;
            public override bool IsFocus => fakeIsFocus;
            public override eSpellTarget Target => fakeTarget;
            public override int Frequency => fakeFrequency;
            public override int Range => fakeRange;
        }

        private class FakePlayerSpy : FakePlayer
        {
            public DOLEvent lastNotifiedEvent;
            public EventArgs lastNotifiedEventArgs;

            public FakePlayerSpy() : base()
            {
                fakeCharacterClass = new DefaultCharacterClass();
                fakeRegion.FakeElapsedTime = 0;
            }

            public override void Notify(DOLEvent e, object sender, EventArgs args)
            {
                lastNotifiedEvent = e;
                lastNotifiedEventArgs = args;
                base.Notify(e, sender, args);
            }
        }

        private class GameEventMgrSpy : GameEventMgr
        {
            public System.Collections.Generic.Dictionary<object, DOLEventHandlerCollection> GameObjectEventCollection => m_gameObjectEventCollections;
        
            public static GameEventMgrSpy LoadAndReturn()
            {
                var spy = new GameEventMgrSpy();
                LoadTestDouble(spy);
                return spy;
            }
        }
    }
}
