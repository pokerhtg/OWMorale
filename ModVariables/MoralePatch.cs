
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
           
            public const string DEFAULTRP = "80";
            public const int MORALE_DIVISOR = 10;
            public static ItemType itemUnitMorale = (ItemType)1000;
            public const int MAXGOODMORALE =170;

            //morale update parameters
            public static int generalDelta = 40; //RP boost if has general
            public static int tribalHonor = 60; //RP boost if a unit belongs to diplomatic tribe
            public static int perLevel = 10;
            public static int recoveryPercent = 8;
            public static int moralePerKill = 40;
            public static int friendDeathMoraleImpact = -40;
            public static int perFamilyOpinion = 10;
            private static int getMoraleChange(int dmg) => -3 * (dmg - 2) - (int)(dmg > 7 ? dmg * (0.6 + dmg / 10.0) : 0);

            public const int MAXTICK = 8; //morale bar's number of ticks
            public const int MORALEPERTICK = 17;
            private const int DECAYPERCENT = 30;
            public static int maroleAtFullBar = MORALEPERTICK * MAXTICK;

            [HarmonyPatch(typeof(Game),"doTurn")]
            static void Postfix(Game __instance)
            {
                foreach (Unit unit in __instance.getUnits().ToSet<Unit>())//create a copy, since the list will be modified as we go through it
                {
                    if (unit == null || !unit.canDamage())
                        continue;
                    if (debug) 
                        Log("doing morale for a unit");
                    //should have morale, but don't
                    if (string.IsNullOrEmpty(unit.getModVariable(RP)) || string.IsNullOrEmpty(unit.getModVariable(MORALE))) 
                        initializeMorale(unit, DEFAULTRP);

                    //should have morale, and do
                    else moraleTurnUpdate(unit, out _);  
                }
            }
          
            [HarmonyPatch(typeof(City), nameof(City.doRebelUnit))]
            static void Postfix(ref Unit __result)
            {
                initializeMorale(__result, DEFAULTRP, 150);
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
                                    changeMorale(friend, boost? -moraleHit: moraleHit, true, noKill: true); //morale blast doesn't kill; stop chaining
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
                        changeMorale(__instance, moraleFromKills(1), false);
                }

                private static int inflictBattleMoraleDamage(ref Unit target, int idamage)
                {
                    if (target.getHP() < 1) //don't change morale given unit is dead; return the estimate
                        return getMoraleChange(idamage);
                    if (debug) 
                        Log(idamage + " change for " + target.getID() + " whose morale is " + target.getModVariable(MORALE));
                    if (int.TryParse(target.getModVariable(MORALE), out _))
                        if (idamage > 0) 
                        {
                            int moraleDelta = getMoraleChange(idamage); //is a negative number
                            var city = target.tile().city();
                            if (city != null)
                            {
                                int cityHP = city.getHP();
                                int maxHP = Math.Max(1, city.getHPMax());
                                moraleDelta = cityHP / 5 + moraleDelta * (maxHP - cityHP) / maxHP; //city provides a boost per attack to morale based on cityHP, and reduce morale damage by a percent based on city's HP percent
                            }
                            changeMorale(target, moraleDelta, false); //change morale; return the result
                           
                            return moraleDelta;
                        }
                    return 0; //unit doesn't have a morale, return 0
                }

                [HarmonyPatch(nameof(Unit.setModVariable))]
                // public virtual void setModVariable(string zIndex, string zNewValue)
                static void Prefix(ref Unit __instance, string zIndex, ref string zNewValue)
                {
                    if (debug)
                        Log("setting " + zIndex);
                    if (zIndex == MORALE)
                        setMorale(__instance, int.Parse(zNewValue), true); //TODO add error checking?                 }
                }

                [HarmonyPatch(nameof(Unit.getModVariable))]
                //public virtual string getModVariable(string zIndex)
                static void Postfix(ref String __result, ref Unit __instance, string zIndex)
                {
                    if (debug)
                        Log("getting " + zIndex);
                    if (__result == null || __instance == null)
                        return;
                    try
                    {
                        if (zIndex == RP && !string.IsNullOrEmpty(__result))
                        {

                            //calculated on the spot. TODO actually save and push changes to actions that changes it
                            calculateRP(__instance, out int total);
                            __result = total.ToString();

                        }
                    }
                    catch (Exception e)
                    { //log any issues?
                         LogError("getModVariable error: " + e.StackTrace);
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
                static void Postfix(Unit __result, TribeType eTribe, bool bEnlisted)
                // public virtual Unit convert(PlayerType ePlayer, TribeType eTribe, bool bEnlisted = false)
                {
                    if (!__result.canDamage())
                        return;
                    var initMoralePercent = 80;
                    if (eTribe == __result.game().infos().Globals.RAIDERS_TRIBE)
                    {
                        initMoralePercent *= 2;
                    }
                    if (bEnlisted)
                    {
                        initMoralePercent = 100;
                    }
                    if (!int.TryParse(__result.getModVariable(MORALE), out int curr) || curr < int.Parse(DEFAULTRP) * initMoralePercent / 100)
                        initializeMorale(__result, DEFAULTRP, initMoralePercent);
                }

                [HarmonyPatch("getMercenaryCostMultiplier")]
                static void Postfix(ref Unit __instance, ref int __result)
                // public virtual Unit convert(PlayerType ePlayer, TribeType eTribe, bool bEnlisted = false)
                {
                    if (!int.TryParse(__instance.getModVariable(MORALE), out int morale))
                        return;
                    if (debug)
                        Log("morale at this point is " + morale);

                    __result *= 50 - moraleEnlistChance(morale); //moraleenglist chance is up to 25%; so this represents 50% discount at most, no upper limit on how extra expensive this can be if morale is crazy high
                    __result /= 230;
                    __result *= 5;//basically divide by 50 overall, rounded 
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

           // [HarmonyPatch(typeof(Unit.UnitAI))]
            public class AIPatch
            {
                [HarmonyPatch(nameof(Player.PlayerAI.updateTileProtection))]
                static bool Prefix(Unit pUnit, int iChange)
               // public virtual void updateTileProtection(Unit pUnit, Tile pUnitTile, int iChange)
                {
                    if (iChange < 0 || pUnit == null || isMoralelyBankrupt(pUnit, 25))
                        return false; //if morale is too long, unit does not provide any positive change to tile protection
                    return true;
                }
            [HarmonyPatch(nameof(Unit.UnitAI.isBelowHealthPercent))]
                ///public virtual bool isBelowHealthPercent(int iPercent)
                ///factoring low morale in "low heal" definition
                static bool Prefix(ref Unit ___unit, ref bool __result, int iPercent)
                {
                    if (debug)
                        Log("unit AI below health percent manipulation");
                    __result = isMoralelyBankrupt(___unit, iPercent/2);//if morale is lower than X / 2 percent, the unit is considered to be less than x % HP for AI eval purposes
                    
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
                        __result *= 3;
                    }
                }

                [HarmonyPatch(typeof(Unit.UnitAI), "doHeal")]
                //protected virtual bool doHeal(PathFinder pPathfinder, bool bRetreat)
                static bool Prefix(ref Unit.UnitAI __instance, ref Unit ___unit, ref bool __result)
                {
                    if (debug)
                        Log("AI doing heal");
                    if (isMoralelyBankrupt(___unit, 20))
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

                [HarmonyPatch(nameof(Unit.UnitAI.expectedDamage))]
                static void Postfix(ref Unit ___unit, ref int __result, Tile pTile)
                //public virtual int expectedDamage(Tile pTile)
                {
                    if (isMoralelyBankrupt(___unit, 15))
                        __result += 2; //expect some attrition
                    if (isMoralelyBankrupt(___unit, 40) && pTile.owner() != ___unit.player())
                        __result++; //expect morale decline, will call that a damage of sort
                }

                [HarmonyPatch(nameof(Unit.UnitAI.retreatTileValue))]
                static void Postfix(ref Unit ___unit, ref long __result, Tile pTile)
                {
                    string m = ___unit.getModVariable(MORALE);
                    if (string.IsNullOrEmpty(m))
                        return;
                   
                    string r = ___unit.getModVariable(RP);
                    if (string.IsNullOrEmpty(r))
                        return;

                    int morale = int.Parse(m);
                    int rp = int.Parse(r);

                    if (rp > morale && ___unit.AI.canHeal(pTile)) //morale can improve with rest
                    {
                        __result *= 2;
                    }
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
                    if (!int.TryParse(unit.getModVariable(MORALE), out int morale) || !int.TryParse(unit.getModVariable(RP), out int rpV))
                    {
                        return false; //no morale, can't bankrupt
                    }
                    return (morale - bar) * 100 / rpV < bar; //morale, not counting the last BAR, is less than BAR percent of RP
                }
            }//end of AI

            private static bool getMoraleBarColors(Unit unit, List<ColorType> aeColors, int damage)
            {
                if (String.IsNullOrEmpty(unit.getModVariable(MORALE)))
                    return false;
                var infos = unit.game().infos();
                int morale = int.Parse(unit.getModVariable(MORALE));

                ColorType moraleColor = colorizeMorale(morale, infos, true);
                int currTicks = morale / MORALEPERTICK;
                int dmgTicks = currTicks - (morale - damage) / MORALEPERTICK;

                for (int i = 0; i < MAXTICK; i++)
                {
                    if (i == currTicks - dmgTicks) //damage preview; from this momennt on all bars are damaged
                    {
                        moraleColor = infos.Globals.COLOR_DAMAGE;
                    }
                    else if (i >= MAXTICK - (morale - maroleAtFullBar) / (3*MORALEPERTICK/2)) //too high; start to double back with cover the top color. Morale per tick increases 50%
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
                        unitTag.SetInt("Morale-Count", morale / MORALEPERTICK);
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
            private static List<int> calculateRP(Unit unit, out int sum, int maxAllowed = MAXGOODMORALE)
            {
                if (!int.TryParse(unit.getModVariable(RPEXTRA), out int extraRP))
                    extraRP = 0;
                else 
                    extraRP *= MORALE_DIVISOR; //extra RP is stored in a non-divided format, so we need to multiply it by the divisor
                var tribe = unit.getTribe();
                var btribe = (tribe != TribeType.NONE && unit.game().infos().tribe(tribe).mbDiplomacy);
                List<int> why = new List<int> {
                    int.Parse(DEFAULTRP),
                    extraRP,
                    unit.hasGeneral() ? generalDelta : 0,
                    perLevel * (unit.getLevel() - 1),
                    unit.hasFamilyOpinion() ? ((int)unit.getFamilyOpinion() - 3) * perFamilyOpinion : 0,
                    btribe ? Math.Max(5, tribalHonor - MORALE_DIVISOR * unit.game().tribe(tribe).getNumTribeImprovements()) : 0,
            };
                sum = 0;

                for (int i = 0; i < why.Count; i++) { 
                    sum += why[i];
                   // Log(sum + " after " + i);
                }
                sum = Math.Min(maxAllowed, sum);
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

                    var whyRP = calculateRP(pUnit, out int sum); // why: base, extra, general, level, familyOpinion, tribalhonor
                    builder.AddTEXT("TEXT_HELPTEXT_UNIT_MORALE_REST_POINT", (float) sum / MORALE_DIVISOR);
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
                        if (whyRP[5] != 0)
                        {
                            builder.Add(helpText.buildColonSpaceOne(helpText.buildSignedTextVariable(whyRP[5], iMultiplier: MORALE_DIVISOR), helpText.TEXTVAR_TYPE("TEXT_HELPTEXT_TRIBAL")));
                        }
                    }
                    if (sum >= MAXGOODMORALE)
                    {
                        using (builder.BeginScope(TextBuilder.ScopeType.DOUBLE_INDENT))
                        {
                            builder.Add(helpText.buildColorTextPositiveVariable(helpText.TEXTVAR_TYPE("TEXT_HELPTEXT_MAX_RP_CAP", MAXGOODMORALE / MORALE_DIVISOR)));
                        }
                    }

                    var totalMoraleUpdate = moraleTurnUpdate(pUnit, out var why, true);
                    //from damage, from recovery, from decay, defStruct, max
                    builder.AddTEXT("TEXT_HELPTEXT_UNIT_MORALE_DRIFT", helpText.buildSignedTextVariable(totalMoraleUpdate, iMultiplier: MORALE_DIVISOR));

                    using (builder.BeginScope(TextBuilder.ScopeType.BULLET))
                    {
                        if (why[1] != 0)
                        {
                            builder.Add(helpText.buildColonSpaceOne(helpText.buildSignedTextVariable(why[1], iMultiplier: MORALE_DIVISOR), helpText.TEXTVAR_TYPE("TEXT_HELPTEXT_UNIT_MORALE_RECOVERY", helpText.buildPercentTextValue(recoveryPercent))));
                        }

                        if (why[3] != 0)
                        {
                            builder.Add(helpText.buildColonSpaceOne(helpText.buildSignedTextVariable(why[3], iMultiplier: MORALE_DIVISOR), helpText.TEXTVAR_TYPE("TEXT_HELPTEXT_UNIT_MORALE_DEF")));
                        }

                        if (why[2] != 0)
                        {
                            builder.Add(helpText.buildColonSpaceOne(helpText.buildSignedTextVariable(why[2], iMultiplier: MORALE_DIVISOR), helpText.TEXTVAR_TYPE("TEXT_HELPTEXT_UNIT_MORALE_DECAY", helpText.buildPercentTextValue(DECAYPERCENT))));
                        }

                        if (why[0] != 0)
                        {
                            builder.Add(helpText.buildColonSpaceOne(helpText.buildSignedTextVariable(why[0], iMultiplier: MORALE_DIVISOR), helpText.TEXTVAR_TYPE("TEXT_HELPTEXT_UNIT_DAMAGE_MORALE", true)));
                        }
                        if (why[4] != 0)
                        {
                            using (builder.BeginScope(TextBuilder.ScopeType.DOUBLE_INDENT))
                            {
                                builder.Add(helpText.buildColorTextPositiveVariable(helpText.TEXTVAR_TYPE("TEXT_HELPTEXT_UNIT_MORALE_CAP", why[4] / MORALE_DIVISOR)));
                            }
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
                    case int n when (n >= 60 && n <= MAXGOODMORALE):
                        return infos.Globals.COLOR_HEALTH_MAX;
                    case int n when (n > MAXGOODMORALE):
                        if (capped)//return highest possible "good" color
                            return infos.Globals.COLOR_HEALTH_MAX;
                        return infos.getType<ColorType>("COLOR_OVERCONFIDENT"); 
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
                    iRP += int.Parse(unitSpecifc) * MORALE_DIVISOR;
                    instance.setModVariable(RP, iRP.ToString());
                }

                bool hasValue = int.TryParse(instance.getModVariable(MORALEEXTRA), out int bonusStart);

                int baseMorale = iRP * initMoralePercent / 100; //80% rp at creation by default
                setMorale(instance, hasValue ? bonusStart * MORALE_DIVISOR + baseMorale : baseMorale);
            }

            public static int moraleTurnUpdate(Unit unit, out List<int> explaination, bool test = false)
            {
                explaination = new List<int> { 0, 0, 0, 0, 0}; //from damage, from recovery, from decay, defStruct, max
                if (!int.TryParse(unit.getModVariable(MORALE), out int iMorale))
                {
                    MohawkAssert.Assert(false, "Morale Parsing failed: " + unit.getModVariable(MORALE) + " on Unit " + unit.getID() + unit.getType());
                    return -1;
                }

                if (!int.TryParse(unit.getModVariable(RP), out int iRP))
                {
                    ////Log("Morale Resting Point Parsing failed: " + unit.getModVariable(RP) + " on a " + unit.game().infos().unit(unit.getType()).mName);
                    return int.Parse(DEFAULTRP);
                }

                int change = 0;
                if (test) //if we are testing, let's include in our prediction future morale-based HP loss's impact on future morale change
                {
                    int extraDamage = unit.game().infos().effectUnit(getMoraleEffect(unit))?.miDamageAlways ?? 0;
                    explaination[0] = -(unit.getDamage() + extraDamage) / 2;
                }
                else
                    explaination[0] = -unit.getDamage() / 2;
                
                if (unit.isHealPossibleTile(unit.tile(), true) && iMorale < iRP)
                {
                    explaination[1] = recoveryPercent * iRP / 100;
                   // change += explaination[1];

                    var tile = unit.tile();
                    if (tile != null)
                    {
                        int defStruct = (tile.improvement()?.miDefenseModifierFriendly ?? 0 + tile.improvement()?.miDefenseModifier ?? 0);
                        defStruct /= 10;
                        defStruct += tile.hasCity() ? recoveryPercent : 0; //city grants double base recovery
                        explaination[3] = defStruct * iRP / 100;
                    }
               //     change += (explaination[3] + explaination[1]) * iRP / 100;
                }
                else if (iMorale > iRP)
                {
                    explaination[2] = DECAYPERCENT * (iRP - iMorale) / 100;//decay; a percent of morale over RP

               //     change += explaination[2];
                }
                for (int i = 0; i< explaination.Count - 1; i++) //exclude the last element of explanation, which is the cap calculated below
                {
                    change += explaination[i];
                }

                if (change > 0 && change + iMorale > iRP) //tried to recover higher than RP
                {
                    explaination[4] = iRP - iMorale - change;
                    change = iRP - iMorale;
                }
               
                if (!test)
                    changeMorale(unit, change);

                return change;
            }

            private static void changeMorale(Unit unit, int delta, bool broadcast = false, bool noKill = false)
            {

                if (!int.TryParse(unit.getModVariable(MORALE), out int iMorale))
                    MohawkAssert.Assert(false, "Current Morale not an int: " + unit.getModVariable(MORALE));
                if (debug)
                    Log(unit.getID() + " initial morale " + iMorale + " about to change by " + delta);

                if (delta != 0)
                {
                    setMorale(unit, Math.Max(iMorale + delta, noKill? 1: 0));

                    //notify morale change
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
