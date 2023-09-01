
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
 Features to do                                             priority
 * defection of low morale units                                9
 * more morale effects                                          4
 * skirmisher ??                                                6
 * test compatiblity with rest of the dynamic mods              3
 * DU + this means leveling up is too good. hmmm.               0
 *         mod DU's hpMax to patch based on morale system
 * MoraleBar mouseover                                          1
 *  
 * AI understanding of morale                                   8
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


        public const int MAXTICK = 8; //morale bar's number of ticks
        public const string DEFAULTRP = "70";
        public const int MORALE_DIVISOR = 10;
        public static ItemType itemUnitMorale = (ItemType) 1000;
    //    public static ItemType itemMoraleHelp = (ItemType)1001;

        //morale update parameters
        public static int generalDelta = 50; //
        public static int perLevel = 10;
        public static int recoveryPercent = 5;
        public static int moralePerKill = 20;
        public static int deathDivisor = 5; // this is 1/x = % RP damage at distance 1 
        public static int perFamilyOpinion = 10;
       
        public static int MORALE_AT_FULL_BAR = 140;
        public static int moralePerTick = MORALE_AT_FULL_BAR/MAXTICK;

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
                        initializeMorale(unit, DEFAULTRP);
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
                        initializeMorale(unit, DEFAULTRP);
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
                    if (zIndex == RP && !string.IsNullOrEmpty(__result)) 
                    {
                        
                       //calculated on the spot. TODO actually save and push changes to actions that changes it
                        calculateRP(__instance, out int total);
                        __result = total.ToString();
                        
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
                    initializeMorale(__instance, DEFAULTRP);
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
                    Game gameClient = pManager.GameClient;
                    if (pWidget.GetWidgetType() == itemUnitMorale)
                    {
                        Unit pUnit = gameClient?.unit(pWidget.GetDataInt(0));
                        if (pUnit != null)
                        {
                            buildUnitMoraleHelp(builder, pUnit, ref __instance);
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

                unitTag = ___APP.UserInterface.GetUIAttributeTag("UnitWidget", unitID);
                updateUnitMoraleBar(pUnit,unitTag, __instance.ColorManager);
            }

          
            private static void updateUnitMoraleBar(Unit pUnit, UIAttributeTag unitTag, ColorManager pManager)
            {
                using (var pipListScoped = CollectionCache.GetListScoped<ColorType>())
                {
                    List<ColorType> aeColors = pipListScoped.Value;

                    string raw = pUnit.getModVariable(MORALE);
                    bool showMoraleBar = getMoraleBarColors(pUnit, aeColors, 0);//pass in morale damage expected; death blast should do this? todo
                    if (showMoraleBar)
                    {
                      
                        int morale = int.Parse(raw);
                      //  int iRP = int.Parse(pUnit.getModVariable(RP));
                        for (int i = 0; i < aeColors.Count; ++i)
                        {
                            UIAttributeTag pipTag = unitTag.GetSubTag("-MoralePip", i);
                            pipTag.SetKey("Color", pManager.GetColorHex(aeColors[i]));

                        }
                        unitTag.SetInt("Morale-Count", morale / moralePerTick);
                        unitTag.SetInt("Morale-Max", MAXTICK); //max morale ticks

                    }
                    unitTag.SetBool("MoraleBar-IsActive", showMoraleBar);
                }
            }

         

            private static bool getMoraleBarColors(Unit unit, List<ColorType> aeColors, int damage)
            {
                if (String.IsNullOrEmpty(unit.getModVariable(MORALE)))
                    return false;
                var infos = unit.game().infos();
                int morale = int.Parse(unit.getModVariable(MORALE));
                
                ColorType moraleColor = colorizeMorale(morale, infos, true); 
                int currTicks = morale / moralePerTick;
                int dmgTicks = currTicks - (morale - damage) / moralePerTick;

                for (int i = 0; i < MAXTICK; i++)
                {
                    if (i == currTicks - dmgTicks) //damage preview; from this momennt on all bars are damaged
                    {
                        moraleColor = infos.Globals.COLOR_DAMAGE;
                    }
                    else if (i == 2 * MAXTICK - currTicks) //willing to do a double-loop to display up to 2x fullbar
                    {
                        moraleColor = colorizeMorale(morale, infos);
                    }

                    aeColors.Add((i < currTicks) ? moraleColor : infos.Globals.COLOR_FORTIFY_NONE);
                }

                return true;
        }
    }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="unit"></param>
        /// <param name="sum"></param>
        /// <returns>why: base, extra, general, level, familyOpinion</returns>
        private static List<int> calculateRP(Unit unit, out int sum)
        {
            if (!int.TryParse(unit.getModVariable(RPEXTRA), out int extraRP))
                extraRP = 0;

            List<int> why = new List<int> {
                    int.Parse(DEFAULTRP), 
                    extraRP,
                    unit.hasGeneral() ? generalDelta : 0,
                    perLevel * (unit.getLevel() - 1),
                    unit.hasFamilyOpinion() ? ((int)unit.getFamilyOpinion() - 3) * perFamilyOpinion : 0,
            };
            sum = 0;
            for (int i = 0; i < why.Count; i++)
                sum += why[i];

            return why;
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

        
        public static TextBuilder buildUnitMoraleHelp(TextBuilder builder, Unit pUnit, ref HelpText helpText)
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

                var whyRP = calculateRP(pUnit, out int sum); // why: base, extra, general, level, familyOpinion
                builder.AddTEXT("TEXT_HELPTEXT_UNIT_MORALE_REST_POINT", (float)sum / MORALE_DIVISOR);  
                using (builder.BeginScope(TextBuilder.ScopeType.BULLET))
                {

                    builder.Add(helpText.buildColonSpaceOne(helpText.buildSignedTextVariable(whyRP[0], iMultiplier: MORALE_DIVISOR), helpText.TEXTVAR_TYPE("TEXT_HELPTEXT_RP_BASE", true))) ;
                    if (whyRP[1] != 0)
                    {
                        builder.Add(helpText.buildColonSpaceOne(helpText.buildSignedTextVariable(whyRP[1], iMultiplier: MORALE_DIVISOR), helpText.TEXTVAR_TYPE("TEXT_HELPTEXT_UNIT_SPECIFIC")));
                    }
                    if (whyRP[2] != 0)
                    {
                        builder.Add(helpText.buildColonSpaceOne(helpText.buildSignedTextVariable(whyRP[2], iMultiplier:MORALE_DIVISOR), helpText.TEXTVAR_TYPE("TEXT_HELPTEXT_RP_GENERAL", true)));
                    }
                    if (whyRP[3] != 0)
                    {
                        builder.Add(helpText.buildColonSpaceOne(helpText.buildSignedTextVariable(whyRP[3], iMultiplier: MORALE_DIVISOR), helpText.TEXTVAR_TYPE("TEXT_HELPTEXT_RP_LEVEL")));
                    }
                    if (whyRP[4] != 0)
                    {
                        builder.Add(helpText.buildColonSpaceOne(helpText.buildSignedTextVariable(whyRP[4], iMultiplier: MORALE_DIVISOR), 
                            helpText.TEXTVAR_TYPE("TEXT_HELPTEXT_RP_FAM_OPINION", helpText.TEXTVAR_TYPE(pUnit.familyOpinion().mName))));
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

            private static ColorType colorizeMorale(int val, Infos infos, bool capped = false)
            {
                switch (val)
                {
                    case int n when (n < 40):
                        return infos.Globals.COLOR_HEALTH_LOW;
                    case int n when (n >= 40 && n < 100):
                        return infos.Globals.COLOR_HEALTH_HIGH;
                    case int n when (n >= 100 && n < 141):
                        return infos.Globals.COLOR_HEALTH_MAX;
                    case int n when (n > 141):
                    if (capped)//return highest possible "good" color
                        return infos.Globals.COLOR_HEALTH_MAX;
                    return infos.getType<ColorType>("COLOR_OVERCONFIDENT"); //todo: find a better color
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
            int baseMorale = iRP * 8 / 10;//80% MORALEEXTRA at creation 
            setMorale(instance,  hasValue ? curr + baseMorale: baseMorale);  
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
                case int n when(n < 30):
                    setMoraleEffect(unit, "EFFECTUNIT_SCATTERING");
                    break;
                case int n when (n >= 30 && n < 60):
                    setMoraleEffect(unit, "EFFECTUNIT_WEAVERING");
                    break;
                case int n when (n >= 60 && n < 90):    
                    setMoraleEffect(unit, "EFFECTUNIT_WEAKENED");
                    break;
                case int n when (n >= 90 && n < 120):
                    setMoraleEffect(unit, "EFFECTUNIT_FRESH");
                    break;
                case int n when (n >= 120 && n < 160):
                    setMoraleEffect(unit, "EFFECTUNIT_EAGER");
                    break;
                case int n when (n >= 160 && n < 200):
                    setMoraleEffect(unit, "EFFECTUNIT_AGGRESSIVE");
                    break;
                case int n when (n >= 200):
                    setMoraleEffect(unit, "EFFECTUNIT_BERSERK");
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
