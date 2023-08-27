
using HarmonyLib;
using Mohawk.SystemCore;
using Mohawk.UIInterfaces;
using System;
using System.Collections.Generic;
using TenCrowns.AppCore;
using TenCrowns.ClientCore;
using TenCrowns.GameCore;
using TenCrowns.GameCore.Text;
using UnityEngine;

namespace ModVariables
{
    public class ModVariablesModEntry : ModEntryPointAdapter
    {
        public const string MY_HARMONY_ID = "harry.moraleSystem";
        public Harmony harmony;

        public override void Initialize(ModSettings modSettings)
        {
            if (harmony != null)
            {
                harmony.UnpatchAll(harmony.Id);
                return;
            }
            harmony = new Harmony(MY_HARMONY_ID);
            harmony.PatchAll();
        }

        public override void Shutdown()
        {
            if (harmony != null)
            {
                harmony.UnpatchAll(MY_HARMONY_ID);
            }
        }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.doTurn), new Type[] { })]

    public class MoraleSystemPatch
    {
        private const string rpStr = "MORALE_REST_POINT";
        private const string moraleStr = "MORALE";
        public static int turnNumber = -1;
        

        static void Prefix(Player __instance)
        {
            Game game = __instance.game();
            
            if (game.getTurn() == turnNumber)
            {
                return;
            }
            turnNumber = game.getTurn();
           
            foreach (Unit unit in game.getUnits())
            {
                if (unit.player() == __instance && unit.isAlive())
                {

                    if (string.IsNullOrEmpty(unit.getModVariable(rpStr)) || string.IsNullOrEmpty(unit.getModVariable(moraleStr)))
                    {
                        MohawkAssert.Assert(false, "found a unit without moraleStr initialization");
                        initializeMorale(unit, "100");
                        return;
                    }       
                    moraleTurnUpdate(unit);
                }
            }   
        }

        [HarmonyPatch(typeof(Unit))]
        public class UnitPatch
        {
            [HarmonyPatch(nameof(Unit.attackUnitOrCity), new Type[] { typeof(Tile), typeof(Player) })]
            ///Testing moraleStr -- track moraleStr only for now

            static void Prefix(ref Unit __instance, ref Tile pToTile, Player pActingPlayer)
            {
                //to do
            }

            [HarmonyPatch(nameof(Unit.setModVariable))]
            // public virtual void setModVariable(string zIndex, string zNewValue)
            static void Postfix(ref Unit __instance, string zIndex, string zNewValue)
            {
                if (zIndex == moraleStr)
                {
                    setMorale(__instance, int.Parse(zNewValue), true); //TODO add error checking?
                }
            }
            [HarmonyPatch(nameof(Unit.getModVariable))]
            //public virtual string getModVariable(string zIndex)
            static void Postfix(ref String __result, ref Unit __instance, string zIndex)
            {
                try
                {
                    if (zIndex == rpStr) //RP is 10pts higher per level of unit, zero indexed
                    {
                        __result = (int.Parse(__result) + 10 * (__instance.getLevel() - 1)).ToString();
                    }
                }
                catch (Exception)
                { //if not ready for this (e.g. game start), just skip it
                  }
            }
            
            [HarmonyPatch(nameof(Unit.start))]
            //public virtual void start(Tile pTile, FamilyType eFamily)
            static void Prefix(ref Unit __instance)
            {
                initializeMorale(__instance, "100");
            }
        }

        [HarmonyPatch(typeof(ClientUI))]
        public class UIPatch
        {
            [HarmonyPatch("updateUnitInfo")]
#pragma warning disable IDE0051 // Remove unused private members
            static void Postfix(ref ClientUI __instance, ref IApplication ___APP, Unit pUnit)
#pragma warning restore IDE0051 // Remove unused private members
            {

                int unitID = pUnit.getID();
                UIAttributeTag unitTag = ___APP.UserInterface.GetUIAttributeTag("Unit", unitID);
               
                unitTag.SetTEXT("Morale", __instance.TextManager, UIPatch.buildMoraleUnitLinkVariable(__instance.HelpText, pUnit));
                unitTag.SetBool("Morale-IsActive", true);
            }

            private static TextVariable buildMoraleUnitLinkVariable(HelpText helpText, Unit pUnit)
            {
                using (new UnityProfileScope("HelpText.buildDefenseUnitLinkVariable"))
                {
                    int morale = int.Parse(pUnit.getModVariable(moraleStr));
                    TextVariable value = helpText.concatenate(helpText.buildValueTextVariable(morale, 10), helpText.ModSettings.SpriteRepo?.GetInlineIconVariable(HUDIconTypes.CAPITAL));
                    return helpText.buildLinkTextVariable(value, buildMoraleHelpText(pUnit), pUnit.getID().ToStringCached(), eLinkColor: colorizeMorale(morale, pUnit.game().infos())); 
                }
            }

            private static String buildMoraleHelpText(Unit pUnit)
            {
                StringBuilder moraleBreakdown = new StringBuilder();
                moraleBreakdown.AppendLine("Current Morale: " + pUnit.getModVariable(moraleStr));
                //todo: add additional info on mouseover helptext
                moraleBreakdown.AppendLine("Morale Rest Point: " + pUnit.getModVariable(rpStr));
                moraleBreakdown.AppendLine("Current effect:" + getMoraleEffect(pUnit));
                return moraleBreakdown.ToString();
            }

            private static ColorType colorizeMorale(int val, Infos infos)
            {
                switch (val)
                {
                    case int n when (n < 50):
                        return infos.Globals.COLOR_DANGER;
                    case int n when (n >= 50 && n < 100):
                        return infos.Globals.COLOR_DAMAGE;
                    case int n when (n >= 100 && n < 150):
                        return infos.Globals.COLOR_HEALTH_HIGH;
                    case int n when (n >= 150):
                        return infos.Globals.COLOR_IDLE; //todo: find a better color
                    default: return infos.Globals.COLOR_WHITE;
                }
            }
        }

        public static void initializeMorale(Unit instance, string restingPoint)
        {
            if (instance.getModVariable(rpStr) == null)
                instance.setModVariable(rpStr, restingPoint); //change me to setMoraleRP
            else
                restingPoint = instance.getModVariable(rpStr);

            if (!int.TryParse(restingPoint, out int iRP))
                MohawkAssert.Assert(false, "Morale Parsing failed at initialization");
          
            bool hasValue = int.TryParse(instance.getModVariable(moraleStr), out int curr);
            setMorale(instance, hasValue? curr: iRP * 6 / 10);  //60% moraleStr at creation 
          
        }

        public static void moraleTurnUpdate(Unit unit)
        {
            if (!int.TryParse(unit.getModVariable(moraleStr), out int iMorale))
                MohawkAssert.Assert(false, "Morale Parsing failed: " + unit.getModVariable(moraleStr));
            if (!int.TryParse(unit.getModVariable(rpStr), out int moraleRP))
                MohawkAssert.Assert(false, "Morale Resting Point Parsing failed: " + unit.getModVariable(rpStr));

            if (unit.isHealPossibleTile(unit.tile(), true) && iMorale < moraleRP)
            {
                changeMorale(unit, Math.Min(moraleRP - iMorale, moraleRP / 10));   //10% recovery rate
            }
            else if (iMorale > moraleRP)
                changeMorale(unit, Math.Min(-5, -unit.getDamage())); 

            if (unit.getName().Contains("MACE"))
            {
                changeMorale(unit, unit.baseStrength() / 2);
                MohawkAssert.Assert(false, String.Format("Debug: MACE morale jumps from {0} to {1}. ", iMorale, (unit.getModVariable(moraleStr))));
            }
        }

        private static void changeMorale(Unit unit, int delta)
        {
            if (!int.TryParse(unit.getModVariable(moraleStr), out int iMorale))
                MohawkAssert.Assert(false, "Current Morale not an int: " + unit.getModVariable(moraleStr));
            if (delta != 0) 
                setMorale(unit, iMorale + delta);
        }


        private static void setMorale(Unit unit, int iMorale, bool bypassSaving = false)
        {
           // MohawkAssert.Assert(iMorale > 110, String.Format("Debug: setting morale went to {0} from {1}. ", iMorale, (unit.getModVariable(moraleStr))));
           
            switch (iMorale)
            {
                case int n when(n < 50):
                    setMoraleEffect(unit, "EFFECTUNIT_WEAVERING");
                    break;
                case int n when (n >= 50 && n < 100):
                    setMoraleEffect(unit, null);
                    break;
                case int n when (n >= 100 && n < 150):
                    setMoraleEffect(unit, "EFFECTUNIT_FRESH");
                    break;
                case int n when (n >= 150):
                    setMoraleEffect(unit, "EFFECTUNIT_AGGRESSIVE");
                    break;
            }
            if (!bypassSaving)
                unit.setModVariable(moraleStr, iMorale.ToString());
        }

        private static EffectUnitType getMoraleEffect(Unit unit)
        {
            Infos infos = unit.game().infos();
            foreach (EffectUnitType eLoopEffectUnit in unit.getEffectUnits())
            {
                if (infos.effectUnit(eLoopEffectUnit).meClass == infos.getType<EffectUnitClassType>("EFFECTUNITCLASS_MORALE"))
                    return eLoopEffectUnit;
            }
            return EffectUnitType.NONE;
        }
        private static void setMoraleEffect(Unit unit, string eff)
        {        
            Infos infos = unit.game().infos();
            EffectUnitType moraleEff = eff == null? EffectUnitType.NONE: infos.getType <EffectUnitType>(eff);
            EffectUnitType oldMoraleEff = EffectUnitType.NONE;
            bool noChange = false;
            EffectUnitClassType moraleSystem = infos.getType<EffectUnitClassType>("EFFECTUNITCLASS_MORALE");

            foreach (EffectUnitType eLoopEffectUnit in unit.getEffectUnits())
            {
                if (infos.effectUnit(eLoopEffectUnit).meClass == moraleSystem)
                {
                    if (eLoopEffectUnit == moraleEff)
                        noChange = true;
                    else
                    { 
                        MohawkAssert.Assert(oldMoraleEff == EffectUnitType.NONE, String.Format("found multiple morale effects.{0} & {1} ", oldMoraleEff, eLoopEffectUnit));
                        oldMoraleEff = eLoopEffectUnit;
                    }
                } 
            }
            if (oldMoraleEff != EffectUnitType.NONE)
                unit.changeEffectUnit(oldMoraleEff, -1, true);
            if (!noChange && moraleEff != EffectUnitType.NONE)
                unit.changeEffectUnit(moraleEff, 1, false);

        //    MohawkAssert.Assert(oldMoraleEff == moraleEff, String.Format("Debug: {1} Effect changed to {0} from {2}. ", eff, unit.getName(), oldMoraleEff));
        }

    }
}
