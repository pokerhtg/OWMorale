
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

/**
 * Features to do
 * defection of low morale units
 * 
*/
namespace ModVariables
{
    public class ModVariablesModEntry : ModEntryPointAdapter
    {
        public const string MY_HARMONY_ID = "harry.moraleSystem.patch";
        public static Harmony harmony;

        public override void Initialize(ModSettings modSettings)
        {
            if (harmony != null)
            {
                Shutdown();
            }
               
            harmony = new Harmony(MY_HARMONY_ID);
            harmony.PatchAll();
        }

        public override void Shutdown()
        {
            if (harmony == null)
                return;
            harmony.UnpatchAll(MY_HARMONY_ID);
            harmony = null;
        }
    }

    [HarmonyPatch]
    public class MoraleSystemPatch
    {
        private const string RPEXTRA = "MORALE_REST_POINT_EXTRA";
        private const string MORALEEXTRA = "MORALE_EXTRA";
        private const string RP = "CURR_REST_POINT_EXTRA";
        private const string MORALE = "CURR_MORALE";

        public const string DEFAULTMORALE = "100";
        public const int MORALE_DIVISOR = 10;
        public static ItemType itemUnitMorale = (ItemType) 1000;

        //morale update parameters
        public static int generalDelta = 50;
        public static int perLevel = 10;
        public static int recoveryPercent = 10;
        public static int moralePerKill = 20;
        public static int deathDivisor = 5;

        [HarmonyPatch(typeof(Player), nameof(Player.doTurn))]
        static void Prefix(Player __instance)
        {
            Game game = __instance.game();
             foreach (Unit unit in game.getUnits())
            {
                if (unit.player() == __instance && unit.isAlive() && unit.canDamage())
                {
                    if (string.IsNullOrEmpty(unit.getModVariable(RP)) || string.IsNullOrEmpty(unit.getModVariable(MORALE)))
                    {
                        MohawkAssert.Assert(false, "found a unit without MORALE initialization");
                        initializeMorale(unit, DEFAULTMORALE);
                        return;
                    }
                    moraleTurnUpdate(unit, out _);
                }
            }   
        }
        [HarmonyPatch(typeof(Game), "doTurn")]
        static void Prefix(Game __instance)
        {
            foreach (Unit unit in __instance.getUnits())
            {
                if (unit.isTribe() && unit.isAlive() && unit.canDamage())
                {
                    if (string.IsNullOrEmpty(unit.getModVariable(RP)) || string.IsNullOrEmpty(unit.getModVariable(MORALE)))
                    {
                        MohawkAssert.Assert(false, "found a unit without MORALE initialization");
                        initializeMorale(unit, DEFAULTMORALE);
                        return;
                    }
                    moraleTurnUpdate(unit, out _);
                }
            }
        }

        [HarmonyPatch(typeof(Unit))]
        public class UnitPatch
        {
            [HarmonyPatch(nameof(Unit.kill))]
            ///morale hit for friendly nearby units
            static bool Prefix (Unit __instance)             
            {
                //morale blast
                //TODO effectpreview this?
                if (__instance == null)
                    return false;
                __instance.changeDamage(-__instance.getHP());
                using (var listScoped = CollectionCache.GetListScoped<int>())
                {  
                    __instance.tile().getTilesInRange(3, listScoped.Value);
                    
                    foreach (int iLoopTile in listScoped.Value)
                    {
                        Tile pLoopTile = __instance.game().tile(iLoopTile);
                        var friend = pLoopTile.defendingUnit();
                        if (friend == null|| friend.getHP() < 1 || friend.getModVariable(MORALE) == null || friend == __instance)
                            continue;
                        if (__instance.getTeam() == friend.getTeam())
                        {
                            int distance = Math.Max(__instance.tile().distanceTile(pLoopTile), 1); //will treat same tile as adj
                            if (!int.TryParse(__instance.getModVariable(RP), out int rp))
                                rp = 0;
                            int moraleHit = -rp / deathDivisor / distance;
                            changeMorale(friend, moraleHit, true);
                        }
                    }
                }
                return true;
            }

            [HarmonyPatch("doXP")]
            ///doxp is called only for combat xp; nonXP units still calls this. 
            ///Morale boost for Killing a unit
            static void Prefix(Unit __instance, int iKills, ref List<TileText> azTileTexts)
            {
                //Dynamic Units hacked iKills to be a xp defStruct, need to be handled separately
                int killsEstimate = __instance.game().infos().Globals.COMBAT_BASE_XP      //10 for unmodded; 1 for DU
                      * (1 + iKills) / 20;                                                //so about 1 kill in both is 20xp

                changeMorale(__instance, killsEstimate * moralePerKill, true);
            }


            [HarmonyPatch(nameof(Unit.attackUnitOrCity), new Type[] { typeof(Tile), typeof(Player) })]
            static void Prefix(ref Unit __instance, ref Tile pToTile, Player pActingPlayer)
            {
                //todo? kills and death both handled elsewhere. Crit? push? 
            }

            [HarmonyPatch(nameof(Unit.setModVariable))]
            // public virtual void setModVariable(string zIndex, string zNewValue)
            static void Prefix(ref Unit __instance, string zIndex, string zNewValue)
            {
                switch (zIndex)
                {
                    case MORALE:
                        setMorale(__instance, int.Parse(zNewValue), true); //TODO add error checking?
                        break;
                    case RP:
                        //setRP();
                        break;
                }
                
            }

            [HarmonyPatch(nameof(Unit.getModVariable))]
            //public virtual string getModVariable(string zIndex)
            static void Postfix(ref String __result, ref Unit __instance, string zIndex)
            {
                try
                {
                    if (zIndex == RP) 
                    {
                        __result = (int.Parse(__result) + (__instance.hasGeneral()? generalDelta : 0) + perLevel * (__instance.getLevel() - 1)).ToString(); //todo: set it in data and make it helptext breakdown friendly
                        
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
                if (__instance.canDamage())
                    initializeMorale(__instance, DEFAULTMORALE);
            }
        }

        [HarmonyPatch(typeof(HelpText))]
        public class HelpPatch
        {
            [HarmonyPatch(nameof(HelpText.buildWidgetHelp))]
            // public virtual TextBuilder buildWidgetHelp(TextBuilder builder, WidgetData pWidget, ClientManager pManager, bool bIncludeEncyclopediaFooter = true)
            static void Postfix(ref TextBuilder builder, WidgetData pWidget, ClientManager pManager, ref HelpText __instance)
            {
                using (new UnityProfileScope("HelpText.buildWidgetHelp"))
                {
                    Player player = pManager.activePlayer();
                    Game gameClient = pManager.GameClient;
                    if (pWidget.GetWidgetType() == itemUnitMorale)
                    {
                        Unit pUnit = gameClient?.unit(pWidget.GetDataInt(0));
                        if (pUnit != null)
                        {
                            buildUnitMoraleHelp(builder, pUnit, player, ref __instance);
                        }
                    }
                }
            }
        }

        [HarmonyPatch(typeof(ClientUI))]
        public class UIPatch
        {
            [HarmonyPatch("updateUnitInfo")]
            static void Postfix(ref ClientUI __instance, ref IApplication ___APP, Unit pUnit)
            {
                int unitID = pUnit.getID();
                UIAttributeTag unitTag = ___APP.UserInterface.GetUIAttributeTag("Unit", unitID);

                var txtvar = buildMoraleUnitLinkVariable(__instance.HelpText, pUnit);

                if (pUnit.canDamage() && txtvar != null)
                {
                    unitTag.SetTEXT("Morale", __instance.TextManager, txtvar);
                    unitTag.SetBool("Morale-IsActive", true);   
                }
                else
                    unitTag.SetBool("Morale-IsActive", false);
            }
        }

        private static TextVariable buildMoraleUnitLinkVariable(HelpText helpText, Unit pUnit)
        {
                
            if(!int.TryParse(pUnit.getModVariable(MORALE), out int morale))
            {
                return null;
            }    
            TextVariable value = helpText.concatenate(helpText.buildValueTextVariable(morale, 10), helpText.ModSettings.SpriteRepo?.GetInlineIconVariable(HUDIconTypes.CAPITAL));
          
            return helpText.buildLinkTextVariable(value, itemUnitMorale, pUnit.getID().ToStringCached(),eLinkColor: colorizeMorale(morale, pUnit.game().infos()));
        }

        
        public static TextBuilder buildUnitMoraleHelp(TextBuilder builder, Unit pUnit, Player pActivePlayer, ref HelpText helpText)
        {
                
            using (new UnityProfileScope("HelpText.buildUnitMoraleHelp"))
            {
                
                var morale = helpText.buildValueTextVariable(int.Parse(pUnit.getModVariable(MORALE)), MORALE_DIVISOR);
                Infos infos = pUnit.game().infos();
                float restingPoint = float.Parse(pUnit.getModVariable(RP)) / MORALE_DIVISOR; //not doing additional calc, just saving it as a de facto string
                
                builder.AddTEXT("TEXT_HELPTEXT_UNIT_MORALE", morale);
                using (builder.BeginScope(TextBuilder.ScopeType.BULLET))
                {
                    var eff = getMoraleEffect(pUnit);
                    if (eff != EffectUnitType.NONE)
                    {
                        builder.Add(helpText.buildEffectUnitLinkVariable(eff));
                    }
                }
               
                builder.AddTEXT("TEXT_HELPTEXT_UNIT_MORALE_REST_POINT", restingPoint);  
                using (builder.BeginScope(TextBuilder.ScopeType.BULLET))
                {
                    builder.Add(helpText.buildColonSpaceOne(helpText.buildSignedTextVariable(int.Parse(DEFAULTMORALE), iMultiplier: MORALE_DIVISOR), helpText.TEXTVAR_TYPE("TEXT_HELPTEXT_RP_BASE", true))) ;
                    if (pUnit.hasGeneral())
                    {
                        builder.Add(helpText.buildColonSpaceOne(helpText.buildSignedTextVariable(generalDelta, iMultiplier:MORALE_DIVISOR), helpText.TEXTVAR_TYPE("TEXT_HELPTEXT_RP_GENERAL", true)));
                    }
                    if (pUnit.getLevel() > 1)
                    {
                        builder.Add(helpText.buildColonSpaceOne(helpText.buildSignedTextVariable(pUnit.getLevel() - 1), helpText.TEXTVAR_TYPE("TEXT_HELPTEXT_RP_LEVEL")));
                    }
                    String unitSpecific = pUnit.getModVariable(RPEXTRA);
                    if (!String.IsNullOrEmpty(unitSpecific))
                    {
                        builder.Add(helpText.buildColonSpaceOne(helpText.buildSignedTextVariable(int.Parse(unitSpecific), iMultiplier: MORALE_DIVISOR), helpText.TEXTVAR_TYPE("TEXT_HELPTEXT_UNIT_SPECIFIC")));
                    }
                }
                
                var totalMoraleUpdate = moraleTurnUpdate(pUnit, out var why, true);
                builder.AddTEXT("TEXT_HELPTEXT_UNIT_MORALE_DRIFT", helpText.buildSignedTextVariable(totalMoraleUpdate, iMultiplier: MORALE_DIVISOR));

                using (builder.BeginScope(TextBuilder.ScopeType.BULLET))
                {
                    if (why[1] != 0)
                    {
                        builder.Add(helpText.concatenate(helpText.buildPercentTextValue(why[1]), helpText.TEXTVAR_TYPE("TEXT_HELPTEXT_UNIT_MORALE_RECOVERY")));
                    }
                    
                    if (why[3] != 0)
                    {
                        builder.Add(helpText.concatenate(helpText.buildPercentTextValue(why[3]), helpText.TEXTVAR_TYPE("TEXT_HELPTEXT_UNIT_MORALE_DEF")));
                    }
                    
                    if (why[2] != 0)
                    {
                        builder.Add(helpText.concatenate(helpText.buildPercentTextValue(why[2]), helpText.TEXTVAR_TYPE("TEXT_HELPTEXT_UNIT_MORALE_DECAY")));
                    }
                    
                    if (pUnit.isDamaged())
                    {
                        builder.Add(helpText.buildColonSpaceOne(helpText.buildSignedTextVariable(why[0], iMultiplier: MORALE_DIVISOR), helpText.TEXTVAR_TYPE("TEXT_HELPTEXT_LINK_HELP_IMPROVEMENT_BUILD_TURNS_DAMAGED", true)));
                    }
                    if (why[4]> -1)
                    {
                        builder.Add(helpText.buildColonSpaceOne(why[4], helpText.buildValueTextVariable(int.Parse("TEXT_HELPTEXT_UNIT_MORALE_CAP"), MORALE_DIVISOR)));
                    }

                }

            }
            return builder;
        }

            private static ColorType colorizeMorale(int val, Infos infos)
            {
                switch (val)
                {
                    case int n when (n < 50):
                        return infos.Globals.COLOR_DAMAGE;
                    case int n when (n >= 50 && n < 100):
                        return infos.Globals.COLOR_HEALTH_LOW;
                    case int n when (n >= 100 && n < 150):
                        return infos.Globals.COLOR_HEALTH_HIGH;
                    case int n when (n >= 150):
                        return infos.Globals.COLOR_HEALTH_MAX; //todo: find a better color
                    default: return infos.Globals.COLOR_WHITE;
                }
            }
        

        public static void initializeMorale(Unit instance, string defaultRP)
        {
            if (!int.TryParse(defaultRP, out int iRP))
                MohawkAssert.Assert(false, "Morale Parsing failed at initialization");

            String unitSpecifc = instance.getModVariable(RPEXTRA);
            if (unitSpecifc == null)
                instance.setModVariable(RP, defaultRP); //change me to setMoraleRP
            else
            {
                iRP += int.Parse(unitSpecifc);
                instance.setModVariable(RP, iRP.ToString());
            }

            bool hasValue = int.TryParse(instance.getModVariable(MORALEEXTRA), out int curr);
            int baseMorale = iRP * 6 / 10;
            setMorale(instance,  hasValue ? curr + baseMorale: baseMorale);  //60% MORALEEXTRA at creation 
        }

        public static int moraleTurnUpdate(Unit unit, out List<int> explaination, bool test = false)
        {
            if (!int.TryParse(unit.getModVariable(MORALE), out int iMorale))
                MohawkAssert.Assert(false, "Morale Parsing failed: " + unit.getModVariable(MORALE));
            if (!int.TryParse(unit.getModVariable(RP), out int iRP))
                MohawkAssert.Assert(false, "Morale Resting Point Parsing failed: " + unit.getModVariable(RP));

            explaination = new List<int> { 0,0,0,0, -1 }; //from damage, from recovery, from decay, defStruct, max
            int change = -unit.getDamage();
            explaination[0] = change;
            if (unit.isHealPossibleTile(unit.tile(), true) && iMorale < iRP)
            {
                int recovery = iRP * recoveryPercent / 100;
                explaination[1] = recoveryPercent;
                change += recovery;

                var tile = unit.tile();
                if (tile != null)
                {
                    int defStruct =  tile.hasCity() ? 100 : 0;
                    defStruct += (tile.improvement()?.miDefenseModifierFriendly ?? 0 + tile.improvement()?.miDefenseModifier ?? 0);
                    defStruct /= 10;
                    explaination[3] = defStruct;
                    change += defStruct * iRP / 100;
                }
            }
            else if (iMorale > iRP)
            {
                explaination[2] = -recoveryPercent / 2;//magic number here
                explaination[2] = Math.Max(iRP - iMorale, explaination[2]); 
                change += explaination[2]; 
            }
            int cap = Math.Min(iRP - iMorale, change);

            if (cap > -1 && cap < change)
            {
                change = cap;
                explaination[4] = cap;
            }
            if (!test)
                changeMorale(unit, change);

            return change;
        }

       /** public static int getMoraleRP(Unit unit)
        {
            String unitExtra = unit.getModVariable(RPEXTRA);
            if (String.IsNullOrEmpty(unitExtra))
            {
                    }
            return -1;
        }
        public static void setMoraleRP(Unit unit, int morale)
        {

        }
       **/
        private static void changeMorale(Unit unit, int delta, bool boradcast = false)
        {
          
            if (!int.TryParse(unit.getModVariable(MORALE), out int iMorale))
                MohawkAssert.Assert(false, "Current Morale not an int: " + unit.getModVariable(MORALE));
            if (delta != 0)
            {
               
                setMorale(unit, Math.Max(iMorale + delta, 0));
                if (iMorale + delta < 1) //already dead
                    return;
                if (delta / MORALE_DIVISOR == 0 || unit.getHP() < 1) //unit could have died from morale-deletion
                    return;
                var msg = (delta / MORALE_DIVISOR).ToString("+#;-#") + " MP";
                if (boradcast)
                {
                    SendTileTextAll(msg, unit.getTileID(), unit.game());
                }
                else if (unit.hasPlayer())
                { 
                    TileText tileText = new TileText(msg, unit.getTileID(), unit.getPlayer());
                    unit.game().sendTileText(tileText);
                }
            }
        }
        private static void SendTileTextAll(string v, int tileID, Game g)
        {
            for (PlayerType playerType = (PlayerType)0; playerType < g.getNumPlayers(); playerType++)
            {
                g.sendTileText(new TileText(v, tileID, playerType));
            }
        }

        private static void setMorale(Unit unit, int iMorale, bool bypassSaving = false)
        {
            if (unit == null || unit.getHP() <1)
                return;

            if (!bypassSaving) {
                unit.setModVariable(MORALE, iMorale.ToString());
                
               }
           
            switch (iMorale)
            {
                case int n when (n < 1):
                    MoraleDeath(unit);
                    break;
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
            
        }

        private static void MoraleDeath(Unit unit)
        {

            //animate running away, send tile text of disband
            try
            {

            
            SendTileTextAll("disband", unit.getTileID(), unit.game());
            if (unit == null)
                MohawkAssert.Assert(false, "null unit sentenced to morale death");
            else 
                unit.kill();
            }
            catch (Exception)
            { //still having null pointers? I give up
                MohawkAssert.Assert(false, "morale death failed");
            }
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

            foreach (EffectUnitType eLoopEffectUnit in unit.getEffectUnits())
            {
                if (infos.effectUnit(eLoopEffectUnit).meClass == infos.getType<EffectUnitClassType>("EFFECTUNITCLASS_MORALE"))
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
        }

    }
}
