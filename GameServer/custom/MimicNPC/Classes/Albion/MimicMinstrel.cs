using DOL.GS.PlayerClass;

namespace DOL.GS.Scripts
{
    public class MimicMinstrel : MimicNPC
    {
        public MimicMinstrel(byte level) : base(new ClassMinstrel(), level)
        {
            MimicSpec = new MinstrelSpec();

            SpendSpecPoints();
            MimicEquipment.SetMeleeWeapon(this, MimicSpec.WeaponTypeOne, eHand.oneHand);
            MimicEquipment.SetShield(this, 1);
            MimicEquipment.SetInstrumentROG(this, Realm, (eCharacterClass)CharacterClass.ID, Level, eObjectType.Instrument, eInventorySlot.TwoHandWeapon, eInstrumentType.Flute);
            MimicEquipment.SetInstrumentROG(this, Realm, (eCharacterClass)CharacterClass.ID, Level, eObjectType.Instrument, eInventorySlot.DistanceWeapon, eInstrumentType.Drum);
            MimicEquipment.SetInstrumentROG(this, Realm, (eCharacterClass)CharacterClass.ID, Level, eObjectType.Instrument, eInventorySlot.FirstEmptyBackpack, eInstrumentType.Lute);

            eObjectType objectType = eObjectType.Leather;

            if (level >= 20)
                objectType = eObjectType.Chain;
            else if (level >= 10)
                objectType = eObjectType.Studded;

            MimicEquipment.SetArmor(this, objectType);
            MimicEquipment.SetJewelryROG(this, Realm, (eCharacterClass)CharacterClass.ID, Level, eObjectType.Magical);
            RefreshItemBonuses();
            SwitchWeapon(eActiveWeaponSlot.Standard);
            RefreshSpecDependantSkills(false);
            GetTauntStyles();
            SetSpells();
            IsCloakHoodUp = Util.RandomBool();
        }
    }

    public class MinstrelSpec : MimicSpec
    {
        public MinstrelSpec()
        {
            SpecName = "MinstrelSpec";

            int randBaseWeap = Util.Random(1);

            switch (randBaseWeap)
            {
                case 0: WeaponTypeOne = "Slash"; break;
                case 1: WeaponTypeOne = "Thrust"; break;
            }

            int randVariance = Util.Random(4);

            switch (randVariance)
            {
                case 0:
                case 1:
                Add(WeaponTypeOne, 39, 0.6f);
                Add("Instruments", 50, 1.0f);
                Add("Stealth", 21, 0.1f);
                break;

                case 2:
                case 3:
                Add(WeaponTypeOne, 44, 0.6f);
                Add("Instruments", 50, 1.0f);
                Add("Stealth", 8, 0.0f);
                break;

                case 4:
                Add(WeaponTypeOne, 50, 0.6f);
                Add("Instruments", 44, 1.0f);
                Add("Stealth", 8, 0.0f);
                break;
            }
        }
    }
}