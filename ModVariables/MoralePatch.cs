
using HarmonyLib;
using Mohawk.SystemCore;
using Mohawk.UIInterfaces;
using System;
using System.Collections.Generic;
using TenCrowns.GameCore.Text;
using TenCrowns.AppCore;
using TenCrowns.ClientCore;
using TenCrowns.GameCore;
using static UnityEngine.Debug;

/**
 Features to do                                                 Status
 * EffectUnit/promotion morale interaction            Waiting on Modvariable in other XMLs
 * MoraleBar mouseover                                Technical Difficulties
 *  
*/
namespace MoraleSystem
{
    public class MoraleEntry : ModEntryPointAdapter
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

        [HarmonyPatch]
        public class MoraleSystemPatch
        {
            private const string RPEXTRA = "MORALE_REST_POINT_EXTRA";
            private const string MORALEEXTRA = "MORALE_EXTRA";
            private const string RP = "CURR_REST_POINT_EXTRA";
            private const string MORALE = "CURR_MORALE";

            public static bool debug = false;
            public const int MAXTICK = 7; //morale bar's number of ticks
            public const string DEFAULTRP = "80";
            public const int MORALE_DIVISOR = 10;
            public static ItemType itemUnitMorale = (ItemType)1000;

            //morale update parameters
            public static int generalDelta = 40; //RP boost if has general
            public static int perLevel = 10;
            public static int recoveryPercent = 8;
            public static int moralePerKill = 40;
            public static int friendDeathMoraleImpact = -30;
            public static int perFamilyOpinion = 15;
            private static int getMoraleChange(int dmg) => -3 * (dmg - 2) - (int)(dmg > 7 ? dmg * (0.6 + dmg / 10.0) : 0);

            public static int MORALE_AT_FULL_BAR = 126;
            public static int moralePerTick = MORALE_AT_FULL_BAR / MAXTICK;

            [HarmonyPatch(typeof(Player), nameof(Player.doTurn))]
            static void Postfix(Player __instance)
            {
                Game game = __instance.game();

                foreach (Unit unit in game.getUnits().ToSet<Unit>())
                {
                    if (debug)
                        Log("looping in doTurn");
                    if (unit?.player() == __instance && unit.isAlive() && unit.canDamage())
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

            [HarmonyPatch(typeof(Tribe), nameof(Tribe.doTurn))]
            static void Postfix(Tribe __instance)
            {
                using (var unitList = CollectionCache.GetListScoped<int>())
                {

                    foreach (int iUnitID in __instance.getUnits().ToSet())
                    {
                        var unit = __instance.game().unit(iUnitID);
                        if (unit.canDamage())
                            moraleTurnUpdate(__instance.game().unit(iUnitID), out _);
                        else
                        {
                            //  if (debug)
                            Log("somehow I'll, make a Man..rauder, out of " + unit.getType());
                            unit.doUpgrade(__instance.game().infos().getType<UnitType>("UNIT_MARAUDER_1"), null);

                        }
                    }
                }
            }

            [HarmonyPatch(typeof(City), nameof(City.doRebelUnit))]
            static void Postfix(ref Unit __result)
            {
                initializeMorale(__result, DEFAULTRP, 160);
            }



            [HarmonyPatch(typeof(Unit))]
            public class UnitPatch
            {
                [HarmonyPatch(nameof(Unit.killBones))]
                ///morale hit for friendly nearby units
                static bool Prefix(Unit __instance)
                {
                    //morale blast
                    //TODO effectpreview this?
                    return blast(__instance);
                }

                [HarmonyPatch(nameof(Unit.doCooldown))]
                ///morale hit for friendly nearby units
                static bool Prefix(Unit __instance, CooldownType eCooldown)
                {
                    if (__instance.game().infos().Globals.ENLISTED_COOLDOWN == eCooldown)
                        return blast(__instance, true);

                    return true;
                }

                //normal mode, boost= false, morale blast makes all friendly units sad. boost makes friends happy
                private static bool blast(Unit __instance, bool boost = false)
                {
                    if (__instance == null)
                        return false;

                    if (string.IsNullOrEmpty(__instance.getModVariable(RP)))
                        return true; //already marked dead, no need to kill it again
                    if (debug)
                        Log("killing " + __instance.getID() + ", with " + __instance.getHP() + " hp left");
                    __instance.setModVariable(RP, null); //mark it as dying to avoid loops
                    try
                    {
                        using (var listScoped = CollectionCache.GetListScoped<int>())
                        {
                            __instance.tile().getTilesInRange(3, listScoped.Value);
                            var infos = __instance.game().infos();
                            foreach (int iLoopTile in listScoped.Value)
                            {
                                Tile pLoopTile = __instance.game().tile(iLoopTile);
                                if (pLoopTile == null)
                                    continue;
                                var friend = pLoopTile.defendingUnit();
                                if (friend == null || friend.getHP() < 1 || friend.getModVariable(MORALE) == null || friend == __instance || string.IsNullOrEmpty(friend.getModVariable(RP)))
                                    continue;

                                //same team, and at least one of the following:
                                //1) one is not tribe, or 2) if both tribe, they must be the same tribe, or 3) they are both undiplomatic tribes
                                if (__instance.getTeam() == friend.getTeam()
                                    && (__instance.getPlayer() != PlayerType.NONE || friend.getPlayer() != PlayerType.NONE || __instance.getTribe() == friend.getTribe()
                                        || !(infos.tribe(__instance.getTribe()).mbDiplomacy) && !(infos.tribe(friend.getTribe()).mbDiplomacy)))
                                {
                                    int distance = Math.Max(__instance.tile().distanceTile(pLoopTile), 1); //will treat same tile as adj

                                    int moraleHit = friendDeathMoraleImpact / distance;
                                    changeMorale(friend, boost? -moraleHit: moraleHit, true);
                                }
                            }
                        }
                    }
                    catch (Exception)
                    {
                        MohawkAssert.Assert(false, "bill died quietly");
                    }
                    return true;
                }

                [HarmonyPatch("getEnlistOnKillChance")]
                static void Postfix(Unit __instance, ref int __result, Tile pAttackTile)
                {
                    if (!int.TryParse(pAttackTile.defendingUnit()?.getModVariable(MORALE), out int morale))
                    {
                        morale = 100;
                    }
                    __result = __instance.game().infos().utils().range(__result + moraleEnlistChance(morale), 0, 100);
                }

                [HarmonyPriority(1000)]
                [HarmonyPatch(nameof(Unit.attackUnitOrCity), new Type[] { typeof(Tile), typeof(Player) })]
                static void Prefix(ref Unit __instance, ref Tile pToTile, out (Unit, Tile, Tile) __state)
                {
                    __state = (pToTile?.defendingUnit(), pToTile, __instance.tile());
                    if (debug)
                        Log("locking onto unit: " + __state.Item1?.getID());
                }

                [HarmonyPatch(nameof(Unit.attackUnitOrCity), new Type[] { typeof(Tile), typeof(Player) })]
                static void Postfix(ref Unit __instance, (Unit, Tile, Tile) __state)
                {

                    if (debug)
                        Log("changing morale of the defender");
                    var target = __state.Item1;
                    var locale = __state.Item2;
                    var fromTile = __state.Item3;

                    if (target != null && target.isAlive() && target.getHP() > 0 && !string.IsNullOrEmpty(target.getModVariable(RP)))
                    {
                        changeMorale(__instance, -inflictBattleMoraleDamage(ref target, __instance.attackUnitDamage(locale, fromTile, target, false)) / 2); //inflict morale damage based on estimated damage; half of the morale damage is gained as morale boost for attacker
                                                                                                                                                            //way overestimates damage to units in cities; net result is units in cities don't die of HP but of morale. Pretty cool, keeping it for now
                    }
                    if (target != null && (!target.isAlive() || target.getHP() < 1))
                        changeMorale(__instance, moraleFromKills(1), true);
                }

                private static int inflictBattleMoraleDamage(ref Unit target, int idamage)
                {
                    if (target.getHP() < 1) //don't change morale given unit is dead; return the estimate
                        return getMoraleChange(idamage);
                    if (debug) 
                        Log(idamage + " change for " + target.getID() + " whose morale is " + target.getModVariable(MORALE));
                    if (int.TryParse(target.getModVariable(MORALE), out _))
                        if (idamage > 1)
                        {
                            int moraleDelta = getMoraleChange(idamage); //is a negative number
                            
                            int cityMoraleBoost = 0;
                            int UNMITIGATABLE = 6;
                            if (target.tile().hasCity())
                            {
                                int cityHP = target.tile().city().getHP();
                                cityMoraleBoost = cityHP / 3 - (moraleDelta + UNMITIGATABLE) * cityHP / target.tile().city().getHPMax(); //city provides a boost per attack to morale based on cityHP, and reduce morale damage by a percent based on city's HP percent
                            }
                            changeMorale(target, moraleDelta + cityMoraleBoost, false); //change morale; return the result
                           
                            return moraleDelta;
                        }
                    return 0; //unit doesn't have a morale, return 0
                }

                [HarmonyPatch(nameof(Unit.setModVariable))]
                // public virtual void setModVariable(string zIndex, string zNewValue)
                static bool Prefix(ref Unit __instance, string zIndex, string zNewValue)
                {
                    if (debug)
                        Log("setting " + zIndex);
                    switch (zIndex)
                    {
                        case MORALE:
                            setMorale(__instance, int.Parse(zNewValue), true); //TODO add error checking?
                            break;
                        case RPEXTRA:
                            if (!string.IsNullOrEmpty(__instance.getModVariable(RPEXTRA)))
                                return false; //disallow setting it if it's already set; non-overwriteable. 
                            break;
                    }
                    return true;
                }

                [HarmonyPatch(nameof(Unit.getModVariable))]
                //public virtual string getModVariable(string zIndex)
                static void Postfix(ref String __result, ref Unit __instance, string zIndex)
                {
                    if (debug)
                        Log("getting " + zIndex);
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
                static void Postfix(ref Unit __instance)
                {
                    if (debug)
                        Log("unit starting");
                    if (__instance.canDamage())
                    {
                        initializeMorale(__instance, DEFAULTRP);
                    }
                }

                [HarmonyPatch(nameof(Unit.convert))]
                static void Postfix(Unit __result, TribeType eTribe)
                // public virtual Unit convert(PlayerType ePlayer, TribeType eTribe, bool bEnlisted = false)
                {
                    if (!__result.canDamage())
                        return;
                    var initMoralePercent = 80;
                    if (eTribe == __result.game().infos().Globals.RAIDERS_TRIBE)
                    {
                        initMoralePercent *= 2;
                    }

                    if (!int.TryParse(__result.getModVariable(MORALE), out int curr) || curr < int.Parse(DEFAULTRP) * initMoralePercent / 100)
                        initializeMorale(__result, DEFAULTRP, initMoralePercent);
                }

                [HarmonyPatch("getMercenaryCostMultiplier")]
                static void Postfix(ref Unit __instance, ref int __result)
                // public virtual Unit convert(PlayerType ePlayer, TribeType eTribe, bool bEnlisted = false)
                {
                    int morale = int.Parse(__instance.getModVariable(MORALE));
                    if (debug)
                        Log("morale at this point is " + morale);

                    __result *= 50 - moraleEnlistChance(morale); //moraleenglist chance is up to 25%; so this represents 50% discount at most, no upper limit on how extra expensive this can be if morale is crazy high
                    __result /= 50;
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
                        if (pWidget.GetWidgetType() == itemUnitMorale)
                        {
                            Unit pUnit = pManager.GameClient?.unit(pWidget.GetDataInt(0));
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
                /**     
                     [HarmonyPatch(nameof(ClientUI.updateUnitAttackPreviewSelection))]
                     static void Postfix(ref ClientUI __instance, ref IApplication ___APP, UIAttributeTag ___mSelectedPanel)
                     {
                         var ClientMgr = ___APP.GetClientManager();
                         Tile pMouseoverTile = ClientMgr.Selection.getAttackPreviewTile();
                         Unit pMouseoverUnit = ClientMgr.Selection.getAttackPreviewUnit();
                         Unit pSelectedUnit = ClientMgr.Selection.getSelectedUnit();
                         Tile pSelectedTile = pSelectedUnit.tile();

                         int ourMoraleChange = pSelectedUnit.getCounterAttackDamage((pSelectedUnit.canDamageCity(pMouseoverTile)) ? null : pMouseoverUnit, pMouseoverTile);
                         if (!pSelectedUnit.canDamageCity(pMouseoverTile) && pMouseoverUnit != null)
                         {
                             
                             ___mSelectedPanel.SetKey("EnemyUnit-DamageMoralePreviewText", ourMoraleChange != 0 ? ClientMgr.HelpText.TEXT("TEXT_GAME_UNIT_ATTACK_MORALE_DAMAGE", ClientMgr.HelpText.buildSignedTextVariable(ourMoraleChange, iMultiplier:MORALE_DIVISOR)) : "");

                         }

                         ___mSelectedPanel.SetBool("EnemyUnit-DamageMoralePreviewText-IsActive", ourMoraleChange != 0);

                     }**/

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

                    if (debug)
                        Log("unit info updated");
                    updateUnitMoraleBar(pUnit);
                }
            }

            [HarmonyPatch(typeof(Unit.UnitAI))]
            public class AIPatch
            {
             /**   [HarmonyPatch(typeof(Player.PlayerAI), nameof(Player.PlayerAI.getTileDanger))]
                //public virtual int getTileDanger(Tile pTile)
                static void Postfix(ref int __result, ref Player ___player, Tile pTile)
                {

                    if (__result != 0 || pTile == null || ___player == null || pTile.getOwner() == ___player.getPlayer())
                        return;

                    __result = 1; //could still be in friendly (just not owned) or neutral lands + hero, so this is an overestimate...but this method gotta be quick for performance reasons

                }
             **/
                [HarmonyPatch(nameof(Unit.UnitAI.isBelowHealthPercent))]
                ///public virtual bool isBelowHealthPercent(int iPercent)
                ///factoring low morale in "low heal" definition
                static bool Prefix(ref Unit ___unit, ref bool __result, int iPercent)
                {
                    if (debug)
                        Log("unit AI below health percent manipulation");
                    if (!___unit.canDamage())
                        return true;
                    if (!int.TryParse(___unit.getModVariable(MORALE), out int morale))
                    {
                        return true;
                    }
                    //not counting the last 20 morale for emergency, is morale lower than threshhold? 
                    __result = 100 * (morale - 20) < int.Parse(___unit.getModVariable(RP)) * iPercent;
                    return !__result;
                }

                [HarmonyPatch(nameof(Unit.UnitAI.retreatTileValue))]
                //public virtual long retreatTileValue(Tile pTile)
                static void Postfix(ref Unit.UnitAI __instance, ref long __result, Tile pTile)
                {
                    if (debug)
                        Log("unit AI retreat tile value manipulation");
                    if (__instance.canHeal(pTile)) //since can heal means can also boost morale, it is now more important 
                    {
                        __result *= 2;
                    }
                }

                [HarmonyPatch("doHeal")]
                //protected virtual bool doHeal(PathFinder pPathfinder, bool bRetreat)
                static bool Prefix(ref Unit.UnitAI __instance, ref Unit ___unit, ref bool __result)
                {
                    if (debug)
                        Log("AI doing heal");
                    if (isMoralelyBankrupt(___unit, 25))
                    {
                        if (!___unit.isDamaged() && __instance.canHeal(___unit.tile()))
                        {
                            //not damaged, but need morale boost
                            ___unit.setPass(true);
                            __result = true;
                            return false;
                        }
                    }
                    return true;
                }

                [HarmonyPatch(nameof(Unit.UnitAI.isInDanger))]
                static void Postfix(ref Unit ___unit, ref bool __result)
                //public virtual bool isInGraveDanger(Tile pTile, bool bAfterAttack, int iExtraDamage = 0)
                {
                    if (debug)
                        Log("unit AI thinking about graves and dangers therein");
                    if (__result)
                    {
                        if (!___unit.canDamage())
                            return;
                        if (!int.TryParse(___unit.getModVariable(MORALE), out int morale))
                        {
                            return; //no morale, do base logic
                        }
                        else if (morale / 10 + ___unit.getHP() > 25) //magic number; morale + HP is pretty high
                            __result = false;
                        return;
                    }

                    __result = isMoralelyBankrupt(___unit, 15);
                }

                private static bool isMoralelyBankrupt(Unit unit, int bar)
                {
                    if (!unit.canDamage())
                        return false;
                    if (!int.TryParse(unit.getModVariable(MORALE), out int morale))
                    {
                        return false; //no morale, can't bankrupt
                    }
                    return (morale - bar) * 100 / int.Parse(unit.getModVariable(RP)) < bar; //morale, not counting the last BAR, is less than BAR percent of RP
                }
            }//end of AI

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

            private static void updateUnitMoraleBar(Unit pUnit, int dmg = 0)
            {
                using (var pipListScoped = CollectionCache.GetListScoped<ColorType>())
                {
                    List<ColorType> aeColors = pipListScoped.Value;
                    var unitTag = pUnit.game().App.UserInterface.GetUIAttributeTag("UnitWidget", pUnit.getID());
                    string raw = pUnit.getModVariable(MORALE);
                    bool showMoraleBar = getMoraleBarColors(pUnit, aeColors, dmg);//pass in morale damage expected; death blast should do this? todo
                    if (showMoraleBar)
                    {

                        int morale = int.Parse(raw);
                        //  int iRP = int.Parse(pUnit.getModVariable(RP));
                        for (int i = 0; i < aeColors.Count; ++i)
                        {
                            UIAttributeTag pipTag = unitTag.GetSubTag("-MoralePip", i);
                            pipTag.SetKey("Color", pUnit.game().modSettings().ColorManager.GetColorHex(aeColors[i]));

                        }
                        unitTag.SetInt("Morale-Count", morale / moralePerTick);
                        unitTag.SetInt("Morale-Max", MAXTICK); //max morale ticks

                    }
                    unitTag.SetBool("MoraleBar-IsActive", showMoraleBar);
                }
            }
            private static int moraleEnlistChance(int morale)
            {
                return (100 - morale) / 4; //0.25% chance of enlist per mp, centered at 100
            }

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

                if (!int.TryParse(pUnit.getModVariable(MORALE), out int morale))
                {
                    return null;
                }
                if (!int.TryParse(pUnit.getModVariable(RP), out _))
                {
                    return null;
                }

                int mchange = moraleTurnUpdate(pUnit, out _, true);
                TextVariable value = helpText.buildValueTextVariable(morale, MORALE_DIVISOR);
                if (mchange != 0)
                    value = helpText.concatenate(value, helpText.buildColorTextSignedVariable(mchange, iMultiplier: MORALE_DIVISOR));

                value = helpText.concatenate(value, pUnit.game().modSettings().GetSpriteRepo().GetInlineIconVariable(HUDIconTypes.CAPITAL));

                return helpText.buildLinkTextVariable(value, itemUnitMorale, pUnit.getID().ToStringCached(), eLinkColor: colorizeMorale(morale, pUnit.game().infos()));
            }

            //Big morale helptext build out, showing morale, resting point, change each turn and associted breakdowns of each
            public static TextBuilder buildUnitMoraleHelp(TextBuilder builder, Unit pUnit, ref HelpText helpText)
            {

                using (new UnityProfileScope("HelpText.buildUnitMoraleHelp"))
                {

                    Infos infos = pUnit.game().infos();
                    int morale = int.Parse(pUnit.getModVariable(MORALE));
                    builder.AddTEXT("TEXT_HELPTEXT_UNIT_MORALE", helpText.buildValueTextVariable(morale, MORALE_DIVISOR));
                    using (builder.BeginScope(TextBuilder.ScopeType.BULLET))
                    {
                        var eff = getMoraleEffect(pUnit);
                        if (eff != EffectUnitType.NONE)
                        {
                            builder.AddTEXT("TEXT_HELPTEXT_UNIT_MORALE_EFF", helpText.buildEffectUnitLinkVariable(eff));
                        }
                        int enlistEff = moraleEnlistChance(morale);
                        if (enlistEff != 0)
                        {
                            builder.Add(helpText.buildColonSpaceOne(helpText.buildPercentTextValue(enlistEff), helpText.TEXTVAR_TYPE("TEXT_HELPTEXT_UNIT_MORALE_ENLIST", true)));
                        }

                    }

                    var whyRP = calculateRP(pUnit, out int sum); // why: base, extra, general, level, familyOpinion
                    builder.AddTEXT("TEXT_HELPTEXT_UNIT_MORALE_REST_POINT", (float)sum / MORALE_DIVISOR);
                    using (builder.BeginScope(TextBuilder.ScopeType.BULLET))
                    {

                        builder.Add(helpText.buildColonSpaceOne(helpText.buildSignedTextVariable(whyRP[0], iMultiplier: MORALE_DIVISOR), helpText.TEXTVAR_TYPE("TEXT_HELPTEXT_RP_BASE", true)));
                        if (whyRP[1] != 0)
                        {
                            builder.Add(helpText.buildColonSpaceOne(helpText.buildSignedTextVariable(whyRP[1], iMultiplier: MORALE_DIVISOR), helpText.TEXTVAR_TYPE("TEXT_HELPTEXT_UNIT_SPECIFIC", helpText.buildUnitTypeLinkVariable(pUnit.getType(), pUnit.game()))));
                        }
                        if (whyRP[2] != 0)
                        {
                            builder.Add(helpText.buildColonSpaceOne(helpText.buildSignedTextVariable(whyRP[2], iMultiplier: MORALE_DIVISOR), helpText.TEXTVAR_TYPE("TEXT_HELPTEXT_RP_GENERAL", true)));
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

                        if (why[0] != 0)
                        {
                            builder.Add(helpText.buildColonSpaceOne(helpText.buildSignedTextVariable(why[0], iMultiplier: MORALE_DIVISOR), helpText.TEXTVAR_TYPE("TEXT_HELPTEXT_LINK_HELP_IMPROVEMENT_BUILD_TURNS_DAMAGED", true)));
                        }
                        if (why[4] > -1)
                        {
                            builder.Add(helpText.buildColonSpaceOne(helpText.buildSignedTextVariable(why[4], iMultiplier: MORALE_DIVISOR),
                                helpText.TEXTVAR_TYPE("TEXT_HELPTEXT_UNIT_MORALE_CAP", helpText.buildSignedTextVariable(sum, false, MORALE_DIVISOR))));
                        }
                    }
                }
                return builder;
            }

            private static ColorType colorizeMorale(int val, Infos infos, bool capped = false)
            {
                switch (val)
                {
                    case int n when (n < 30):
                        return infos.Globals.COLOR_HEALTH_LOW;
                    case int n when (n >= 30 && n < 60):
                        return infos.Globals.COLOR_HEALTH_HIGH;
                    case int n when (n >= 60 && n <= MORALE_AT_FULL_BAR):
                        return infos.Globals.COLOR_HEALTH_MAX;
                    case int n when (n > MORALE_AT_FULL_BAR):
                        if (capped)//return highest possible "good" color
                            return infos.Globals.COLOR_HEALTH_MAX;
                        return infos.getType<ColorType>("COLOR_OVERCONFIDENT"); //todo: find a better color
                    default: return infos.Globals.COLOR_WHITE;
                }
            }
             

        public static void initializeMorale(Unit instance, string defaultRP, int initMoralePercent = 80)
            {
                if (!int.TryParse(defaultRP, out int iRP))
                    MohawkAssert.Assert(false, "Morale Parsing failed at initialization");

                String unitSpecifc = instance.getModVariable(RPEXTRA);
                if (unitSpecifc == null)
                {
                    instance.setModVariable(RPEXTRA, "0");
                    instance.setModVariable(RP, defaultRP);
                }
                else
                {
                    iRP += int.Parse(unitSpecifc);
                    instance.setModVariable(RP, iRP.ToString());
                }

                bool hasValue = int.TryParse(instance.getModVariable(MORALEEXTRA), out int curr);

                int baseMorale = iRP * initMoralePercent / 100; //80% rp at creation by default
                setMorale(instance, hasValue ? curr + baseMorale : baseMorale);
            }

            public static int moraleTurnUpdate(Unit unit, out List<int> explaination, bool test = false)
            {

                explaination = new List<int> { 0, 0, 0, 0, -1 }; //from damage, from recovery, from decay, defStruct, max
                if (!int.TryParse(unit.getModVariable(MORALE), out int iMorale))
                {
                    MohawkAssert.Assert(false, "Morale Parsing failed: " + unit.getModVariable(MORALE) + " on Unit " + unit.getID() + unit.getType());
                    return -1;
                }

                if (!int.TryParse(unit.getModVariable(RP), out int iRP))
                {
                    MohawkAssert.Assert(false, "Morale Resting Point Parsing failed: " + unit.getModVariable(RP) + " on Unit " + unit.getID() + unit.getType());
                    return -1;
                }
                int change = -unit.getDamage() / 2;
                if (test) //if we are testing, let's include in our prediction future morale-based HP loss's impact on future morale change
                {
                    int extraDamage = unit.game().infos().effectUnit(getMoraleEffect(unit))?.miDamageAlways ?? 0;
                    change = -(unit.getDamage() + extraDamage) / 2;
                }

                explaination[0] = change;
                if (unit.isHealPossibleTile(unit.tile(), true) && iMorale < iRP)
                {

                    explaination[1] = recoveryPercent;


                    var tile = unit.tile();
                    if (tile != null)
                    {
                        int defStruct = (tile.improvement()?.miDefenseModifierFriendly ?? 0 + tile.improvement()?.miDefenseModifier ?? 0);
                        defStruct /= 10;
                        defStruct += tile.hasCity() ? recoveryPercent : 0; //city grants double base recovery
                        explaination[3] = defStruct;
                    }

                    change += (explaination[3] + explaination[1]) * iRP / 100;
                }
                else if (iMorale > iRP)
                {
                    explaination[2] = -recoveryPercent * 4;//magic number here
                    change -= explaination[2] * (iRP - iMorale) / 100;
                }
                int cap = Math.Min(iRP - iMorale, change);

                if (cap > -1 && cap < change)
                {
                    explaination[4] = cap - change;
                    change = cap;
                }
                if (!test)
                    changeMorale(unit, change);

                return change;
            }

            private static void changeMorale(Unit unit, int delta, bool broadcast = false)
            {

                if (!int.TryParse(unit.getModVariable(MORALE), out int iMorale))
                    MohawkAssert.Assert(false, "Current Morale not an int: " + unit.getModVariable(MORALE));
                if (debug)
                    Log(unit.getID() + " initial morale " + iMorale + " about to change by " + delta);

                if (delta != 0)
                {
                    setMorale(unit, Math.Max(iMorale + delta, 0));
                    if (iMorale + delta < 1 || !unit.isAlive() || unit.getHP() < 1) //already dead
                        return;
                    if (delta / MORALE_DIVISOR == 0)
                        return;
                    var msg = (delta / MORALE_DIVISOR).ToString("+#;-#") + "Morale";
                    if (broadcast)
                    {
                        SendTileTextAll(msg, unit.getTileID(), unit.game());
                    }
                    else if (unit.hasPlayer())
                    {
                        TileText tileText = new TileText(msg, unit.getTileID(), unit.getPlayer());
                        unit.game().sendTileText(tileText);
                    }
                }

                if (debug)
                    Log("final morale " + unit.getModVariable(MORALE));

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
                if (unit == null || unit.getHP() < 1)
                    return;

                if (!bypassSaving)
                {
                    unit.setModVariable(MORALE, iMorale.ToString());
                }


                switch (iMorale)
                {
                    case int n when (n < 1):
                        MoraleDeath(unit);
                        break;
                    case int n when (n < 30):
                        setMoraleEffect(unit, "EFFECTUNIT_SCATTERING");
                        break;
                    case int n when (n >= 30 && n < 60):
                        setMoraleEffect(unit, "EFFECTUNIT_WEAVERING");
                        break;
                    case int n when (n >= 60 && n < 100):
                        setMoraleEffect(unit, "EFFECTUNIT_WEAKENED");
                        break;
                    case int n when (n >= 100 && n < 140):
                        setMoraleEffect(unit, "EFFECTUNIT_FRESH");
                        break;
                    case int n when (n >= 140 && n < 180):
                        setMoraleEffect(unit, "EFFECTUNIT_EAGER");
                        break;
                    case int n when (n >= 180 && n < 220):
                        setMoraleEffect(unit, "EFFECTUNIT_AGGRESSIVE");
                        break;
                    case int n when (n >= 220):
                        setMoraleEffect(unit, "EFFECTUNIT_BERSERK");
                        break;
                }
            }

            private static void MoraleDeath(Unit unit)
            {
                //send tile text of disperse
                try
                {
                    if (unit.getHP() == 0)
                        return; //already dead
                    Game g = unit.game();

                    SendTileTextAll("disperse", unit.getTileID(), g);


                    if (unit.hasPlayer())
                    {
                        Player p = unit.player();
                        p.pushLogData(() => g.textManager().TEXT("TEXT_GAME_UNIT_DIED_FROM_MORALE", g.HelpText.buildUnitTypeLinkVariable(unit.getType(), g, unit)),
                                            GameLogType.UNIT_KILLED, unit.getTileID(), unit.GetType(), unit.getTileID());
                    }
                    unit.killBones();

                }
                catch (Exception)
                { //ded
                }
            }

            private static int moraleFromKills(int multiplier)
            {
                return multiplier * moralePerKill;
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
                EffectUnitType moraleEff = eff == null ? EffectUnitType.NONE : infos.getType<EffectUnitType>(eff);
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
                {

                    unit.changeEffectUnit(oldMoraleEff, SourceEffectUnitType.NONE, -1, true);
                }
                if (!noChange && moraleEff != EffectUnitType.NONE)
                {

                    unit.changeEffectUnit(moraleEff, SourceEffectUnitType.NONE, 1, false);
                }

            }
        }
    }
}
