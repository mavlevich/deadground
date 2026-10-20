# Unit data sources

Starting values for the historical unit types (TDD section 17.1). Every number in
`content/coldwar/units.json` must come from this table. A value marked **estimate** has no period
source yet; the note says how it was chosen. Estimates are replaced when a better source is found,
and tuned in play only inside the stated range.

Era: about 1976 to 1982. Speeds are given in km/h as in the sources, with m/s for the data file.
Where historical values and current gameplay values differ, both are documented in separate columns.
Gameplay tuning differences are marked "balance, revisit after play".

## Mobility and protection

| Type | Crew + troops | Weight | Road speed | Off-road speed | Reverse | Water | Sourced Armor front / side / rear (mm RHA) | Game Armor front / side / rear (mm RHA) |
|---|---|---|---|---|---|---|---|---|
| T-62 (1972 model) | 4 | 37 t [1] | 50 km/h, 13.9 m/s [1] | 30 km/h, 8.3 m/s, estimate [2] | 8 km/h, 2.2 m/s, estimate [3] | no (snorkel only) | 200 / 80 / 45 mm [1] [2] [15] | 200 / 80 / 45 mm |
| M60A1 | 4 | 47.7 t [4] | 48 km/h, 13.3 m/s [5] | 25 km/h, 6.9 m/s, estimate [6] | 16 km/h, 4.5 m/s [5] | no | 250 / 76 / 41 mm [4] [6] | 220 / 76 / 41 mm |
| BTR-70 | 2 + 8 [7] | 11.5 t [7] | 80 km/h, 22.2 m/s [7] [8] | 40 km/h, 11.1 m/s, estimate | 15 km/h, 4.2 m/s, estimate | 9 km/h [7] | 10 / 7 / 7 mm [7] [8] | 16 / 9 / 7 mm |
| M113A1 | 2 + 11 [9] | 12.3 t [10] | 64 km/h, 17.8 m/s [9] | 35 km/h, 9.7 m/s, estimate | 15 km/h, 4.2 m/s, estimate | 5.8 km/h [10] | 22 / 15 / 15 mm RHA equiv, estimate [10] | 20 / 12 / 12 mm RHA equiv |
| BRDM-2 | 4 [11] | 7.0 t [11] | 95 km/h, 26.4 m/s [11] | 40 km/h, 11.1 m/s, estimate | 15 km/h, 4.2 m/s, estimate | 10 km/h [11] | 14 / 7 / 5 mm [11] | 16 / 8 / 5 mm |
| M151A2 | 1 + 3 | 1.1 t [14] | 106 km/h, 29.4 m/s, estimate | 50 km/h, 13.9 m/s, estimate | 15 km/h, 4.2 m/s, estimate | no | none (0 / 0 / 0 mm) [14] | none (0 / 0 / 0 mm) |

## Weapons

| Weapon | Mounted on | Effective range used | Sourced Traverse rate | Game Traverse rate | Sourced Penetration (near / far) | Game Penetration (near / far) | Sourced Reload and Burst | Game Reload and Burst | Notes |
|---|---|---|---|---|---|---|---|---|---|
| 115 mm U-5TS (2A20) smoothbore | T-62 | 1,600 m [2] | 18.0 deg/s [15] | 18.0 deg/s | 240 mm (2,000 m), an estimate in [16]; no checked source for shorter ranges | 290 mm (0 m) / 200 mm (1,600 m) | 40 rounds [2], 1 round per burst, reload ~15 s [15] (or 4 rpm, unverified from secondary sources) | 40 rounds, 1 round per burst, 8.5 s reload | Powered electric traverse: 20.0 s for a full 360 deg turn (TRADOC Bulletin 10 [15]). 3BM-6 steel APFSDS round: [16] gives about 240 mm at 2,000 m against vertical RHA and marks it as an estimate. Reload tuned from historical ~15.0 s to 8.5 s for duel parity; balance, revisit after play. |
| 115 mm, sight | T-62 | | | | | | | | FAS: APFSDS "extremely accurate out to a maximum effective range of 1,600 meters". TSh2B-41 sight with stadiametric ranging; the laser rangefinder came with the T-62M in the 1980s [2]. |
| 105 mm M68 rifled | M60A1 | 2,000 m, estimate [12] | 22.5 deg/s [6] | 22.5 deg/s | ~300 mm (0 m) / ~200 mm (2,000 m), unverified, secondary sources citing Hunnicutt [4] [6] | 290 mm (0 m) / 155 mm (2,000 m) | 63 rounds [4] [6], 1 round per burst, 8.5 s reload [17] | 63 rounds, 1 round per burst, 8.5 s reload | Powered hydraulic traverse: 16.0 s for a full 360 deg turn (AFV Database [6]; TDD 9.5 cites TM 9-2350-215-10). M392A2 APDS round chosen as standard US service round for mid-1970s [4] [6]. Normal sustained rate of 6 to 8 rpm gives 8.5 s reload (Warrior Maven [17]). Penetration tuned to 290/155 mm for frontal duel balance with T-62; balance, revisit after play. |
| 14.5 mm KPVT | BTR-70, BRDM-2 | 1,000 m, game value ([8] gives 2,000 m against ground targets) | 15.0 deg/s, estimate | 15.0 deg/s | 32 mm (500 m) / 20 mm (1,000 m), unverified, no checked source | 34 mm (0 m) / 18 mm (1,000 m) | 500 rounds [8], 5 rounds per burst, 1.5 s interval, estimate | 500 rounds, 5 rounds per burst, 1.5 s interval | BPU-1 turret manual handwheel traverse (estimate: 24.0 s for full 360 deg turn under combat conditions). B-32 API figures of 32 mm at 500 m and 20 mm at 1,000 m are widely quoted but were not found in a checked source. Penetration tuned for APC balance; balance, revisit after play. |
| 12.7 mm M2HB | M113A1 | 1,000 m, estimate | 30.0 deg/s, estimate | 30.0 deg/s | 25.4 mm (200 m) / 13 mm (1,000 m), unverified, no checked source | 28 mm (0 m) / 14 mm (1,000 m) | 2,000 rounds [10], 5 rounds per burst, 1.5 s interval, estimate | 2,000 rounds, 5 rounds per burst, 1.5 s interval | M13 cupola manual traverse (estimate: 12.0 s for full 360 deg turn). .50 cal AP M2 / M8 API figures of 25.4 mm at 200 m and about 13 mm at 1,000 m were not found in a checked source. Penetration tuned for APC balance; balance, revisit after play. |
| 7.62 mm M60 MG | M151A2 | 800 m, estimate | 60.0 deg/s, estimate | 60.0 deg/s | 9.0 mm (0-100 m) / 4.0 mm (800 m), estimate, no checked source | 9.0 mm (0 m) / 4.0 mm (800 m) | 1,000 rounds, 5 rounds per burst, 1.5 s interval, estimate | 1,000 rounds, 5 rounds per burst, 1.5 s interval | Pedestal pintle mount, manual traverse (mount type not verified; estimate: 6.0 s for full 360 deg turn). 7.62 mm NATO M80 Ball / M61 AP penetration is an estimate. Added for armed recon balance; balance, revisit after play. |

## Fire accuracy and dispersion

Direct fire accuracy for halted units (TDD section 12.3). Values in `content/coldwar/units.json` define a piecewise linear hit probability curve by range against a fully exposed, stationary target, plus target motion penalties, ranging bonuses, and dispersion for misses.

Period gunnery data: FM 17-12 Tank Gunnery [12] gives stationary tank gun first-round hit probabilities against stationary tank targets around 80% to 85% at 1,000 m for 105 mm guns with optical coincidence rangefinders, dropping sharply past 1,500 m. Soviet 115 mm U-5TS with TSh2B-41 stadiametric sight achieves comparable accuracy out to its 1,600 m effective sabot range (FAS [2]). Machine gun bursts are modeled with one roll per burst, with wider short-range forgiveness.

| Weapon | Mounted on | Accuracy Curve [Range (m): Hit Prob] | Dispersion (mils) | Target Move Penalty | Ranging Bonus (per shot / max) | Source / Rationale |
|---|---|---|---|---|---|---|
| 115 mm U-5TS (2A20) | T-62 | 500: 0.90, 1000: 0.80, 1500: 0.58, 1600: 0.50 | 3.0 | 0.20 | 0.08 / 0.24 | Sourced from FM 17-12 [12] and FAS [2]; APFSDS flat trajectory out to 1,600 m stadiametric limit; 80% first-round hit chance at 1,000 m. |
| 105 mm M68 | M60A1 | 500: 0.92, 1000: 0.82, 1500: 0.62, 2000: 0.40 | 2.8 | 0.20 | 0.08 / 0.24 | Sourced from FM 17-12 [12]; M17 coincidence optical rangefinder provides ~82% first-round hit chance at 1,000 m (pinned within 2% of T-62). |
| 14.5 mm KPVT | BTR-70, BRDM-2 | 200: 0.88, 500: 0.68, 800: 0.48, 1000: 0.35 | 3.5 | 0.15 | 0.06 / 0.18 | Estimate for 5-round bursts against vehicle targets; manual turret mount. |
| 12.7 mm M2HB | M113A1 | 200: 0.88, 500: 0.68, 800: 0.48, 1000: 0.35 | 3.8 | 0.15 | 0.06 / 0.18 | Estimate for 5-round bursts against vehicle targets; cupola mount. |
| 7.62 mm M60 MG | M151A2 | 200: 0.82, 400: 0.60, 600: 0.42, 800: 0.28 | 4.0 | 0.15 | 0.05 / 0.15 | Estimate for 5-round bursts against vehicle targets; pintle mount. |

## Placement estimates

These numbers place sensors and targets on the hull. They come from the vehicle heights and widths
above and from our models, not from a period table.

| Type | Eye height | Target points (low, high) | Muzzle height | Radius (half width) |
|---|---|---|---|---|
| T-62 | 2.3 m | 0.9, 2.2 m | 1.75 m | 1.65 m |
| M60A1 | 3.0 m (cupola) | 1.0, 2.9 m | 2.15 m | 1.8 m |
| BTR-70 | 2.0 m | 0.9, 2.0 m | 1.9 m | 1.4 m |
| M113A1 | 2.2 m | 0.8, 2.3 m | 2.1 m | 1.35 m |
| BRDM-2 | 2.0 m | 0.8, 2.1 m | 1.9 m | 1.2 m |
| M151A2 | 1.8 m | 0.6, 1.5 m | 1.4 m | 0.85 m |

Health and damage with armor rules (TDD section 12.3): tanks 100 health, APCs and BRDM-2 50 health,
M151A2 30 health. Tank guns deal 50 damage on penetration. Heavy machine guns fire 5-round bursts
dealing 10 damage per penetrating burst. Light machine guns fire 5-round bursts dealing 5 damage per
penetrating burst. Partial bursts scale damage proportionally to rounds fired. Non-penetrating hits
deal zero damage. Hull and turret share armor values in M1 as a documented simplification. Contact
tracking in `units.json` (memory, correlation gate) is gameplay tuning for kilometer distances, not period data.

## Detection starting values (estimates)

No public period table of visual detection ranges was found. TOP 2-2-615A describes how to measure
vehicle detectability by observers with and without binoculars [13], but gives no ranges. These
values assume a clear day, open ground and a moving target. Cover, concealment, a halted target,
dust, smoke and weather reduce them later (section 10).

| Observer | Detect |
|---|---|
| Reconnaissance vehicle, halted, with optics | 3,000 m |
| Tank, buttoned up | 2,000 m |
| APC | 1,500 m |

## Duel ranges and balance (pinned in tests)

The following engagement matrix defines the gameplay balance between factions across armor classes.
Both sides are tuned to roughly equal capability in direct duels. These values are pinned in tests
`ArmorAndKpvtTests.DuelRanges_TanksApcAndRecon_Pinned` and `FireAccuracyTests.PinnedDuelAccuracy_ColdWarTanksAt1000m_WithinTenPercentagePoints` so regressions fail the build.
Balance, revisit after play.

| Duel Matchup | Shooter Weapon | Target Aspect & Armor | Penetration Falloff Curve | Penetration Cutoff Range | Penetration at Max Range | Balance Gameplay Outcome |
|---|---|---|---|---|---|---|
| Tank frontal duel (T-62 shooter) | 115 mm U-5TS | M60A1 Front: 220 mm | 290 mm (0 m) to 200 mm (1,600 m) | 1,244 m | n/a | T-62 penetrates M60A1 front inside 1,244 m (window 1,200 to 1,500 m). |
| Tank frontal duel (M60A1 shooter) | 105 mm M68 | T-62 Front: 200 mm | 290 mm (0 m) to 155 mm (2,000 m) | 1,333 m | n/a | M60A1 penetrates T-62 front inside 1,333 m (window 1,200 to 1,500 m, within 7.1% of T-62). |
| Tank flank duel (T-62 shooter) | 115 mm U-5TS | M60A1 Side: 76 mm | 290 mm (0 m) to 200 mm (1,600 m) | > 1,600 m | 200 mm vs 76 mm | T-62 penetrates M60A1 flank/rear at all combat ranges up to max range. |
| Tank flank duel (M60A1 shooter) | 105 mm M68 | T-62 Side: 80 mm | 290 mm (0 m) to 155 mm (2,000 m) | > 2,000 m | 155 mm vs 80 mm | M60A1 penetrates T-62 flank/rear at all combat ranges up to max range. |
| APC frontal duel (BTR-70 shooter) | 14.5 mm KPVT | M113A1 Front: 20 mm | 34 mm (0 m) to 18 mm (1,000 m) | 875 m | n/a | KPVT penetrates M113A1 front at close to medium range (< 875 m). |
| APC frontal duel (M113A1 shooter) | 12.7 mm M2HB | BTR-70 Front: 16 mm | 28 mm (0 m) to 14 mm (1,000 m) | 857 m | n/a | M2HB penetrates BTR-70 front at close to medium range (< 857 m). |
| APC frontal duel (M113A1 shooter) | 12.7 mm M2HB | BRDM-2 Front: 16 mm | 28 mm (0 m) to 14 mm (1,000 m) | 857 m | n/a | M2HB penetrates BRDM-2 front at close to medium range (< 857 m). |
| APC flank duel (BTR-70 shooter) | 14.5 mm KPVT | M113A1 Side: 12 mm | 34 mm (0 m) to 18 mm (1,000 m) | > 1,000 m | 18 mm vs 12 mm | KPVT penetrates M113A1 flank/rear at all ranges up to max range. |
| APC flank duel (M113A1 shooter) | 12.7 mm M2HB | BTR-70 Side: 9 mm | 28 mm (0 m) to 14 mm (1,000 m) | > 1,000 m | 14 mm vs 9 mm | M2HB penetrates BTR-70 flank/rear at all ranges up to max range. |
| APC flank duel (M113A1 shooter) | 12.7 mm M2HB | BRDM-2 Side: 8 mm | 28 mm (0 m) to 14 mm (1,000 m) | > 1,000 m | 14 mm vs 8 mm | M2HB penetrates BRDM-2 flank/rear at all ranges up to max range. |
| Recon duel (M151A2 shooter) | 7.62 mm M60 | BRDM-2 Side: 8 mm | 9 mm (0 m) to 4 mm (800 m) | 160 m | n/a | M151A2 penetrates BRDM-2 flanks at close ambush range (< 160 m). |
| Recon vs armored front | 7.62 mm M60 | APC or Tank Front | 9 mm (0 m) to 4 mm (800 m) | 0 m (never) | n/a | 7.62 mm never penetrates front armor of any armored vehicle (16 to 220 mm). |
| Recon vs soft skin | 7.62 mm M60 | M151A2: 0 mm | 9 mm (0 m) to 4 mm (800 m) | > 800 m | 4 mm vs 0 mm | Penetrates unarmored soft skin targets at all effective ranges. |

- Tank first-shot accuracy duel: at 1,000 m against a halted, fully exposed enemy tank, T-62 first-shot hit probability is 80.0% and M60A1 is 82.0% (difference 2.0%, pinned within 10 percentage points in test `FireAccuracyTests.PinnedDuelAccuracy_ColdWarTanksAt1000m_WithinTenPercentagePoints`).

## Notes

- [2] The Czech technical table gives 30 km/h cross-country; Wikipedia gives 40 km/h [1]. The
  lower value is used because it describes sustained movement, not a peak.
- [3] One reverse gear. The only figure found (8 km/h) is from a game wiki, so it stays an estimate.
- M151A2: about 106 km/h (66 mph) is the commonly quoted top speed, but no primary source was found.
- [6] Wikipedia and secondary sources give 19 km/h cross-country for the M60A1 [4]. 25 km/h is used
  so that the T-62 and M60A1 keep their road speed ratio off-road; to confirm from the TM.
- Turret traverse rates: T-62 electric powered traverse full 360 deg in 20.0 s gives 18.0 deg/s (TRADOC Bulletin 10 [15]).
  M60A1 hydraulic powered traverse full 360 deg in 16.0 s gives 22.5 deg/s (AFV Database [6]; TDD 9.5 cites TM 9-2350-215-10).
  BTR-70 and BRDM-2 BPU-1 manual traverse full 360 deg in 24 s gives 15.0 deg/s estimate (unverified, from secondary sources citing Jane's).
  M113A1 cupola manual traverse full 360 deg in 12 s gives 30.0 deg/s estimate.
  M151A2 pedestal pintle mount full 360 deg in 6 s gives 60.0 deg/s estimate.
  Prototype test tank uses 24.0 deg/s (360 deg in 15.0 s) as a plausible estimate.
- Ammunition and rounds chosen: T-62 carries the 3BM-6 steel APFSDS round ([16] gives about 240 mm at 2,000 m, marked there as an estimate),
  the standard widely stocked Soviet 115 mm kinetic round of the era. M60A1 carries the M392A2 APDS round (~300 mm at 0 m, ~200 mm at 2,000 m
  from secondary sources citing Hunnicutt [4] [6]), the standard US Army 105 mm sabot round in widespread service across
  the 1970s prior to late 1970s fielding of M735. BTR-70 and BRDM-2 fire 14.5 mm B-32 API rounds in 5-round bursts (about 32 mm at 500 m
  and 20 mm at 1,000 m, unverified). M113A1 fires 12.7 mm M8 API / M2 AP rounds in 5-round bursts (about
  25.4 mm at 200 m and 13 mm at 1,000 m, unverified). M151A2 fires 7.62 mm M80 Ball / M61 AP in 5-round bursts (about 9.0 mm at 0-100 m and 4.0 mm at 800 m, estimate).
- Armor values: T-62 hull upper glacis 102 mm at 60 deg provides about 200 mm RHA equivalent frontally [1] [2] [15].
  M60A1 hull upper glacis 109 mm at 65 deg provides about 250 mm RHA equivalent frontally [4] [6].
  M113A1 5083 aluminum alloy provides about 22 mm RHA equivalent frontally and 15 mm on the sides (estimate based on aluminum thickness in [10]).
  In M1, hull and turret share front, side and rear values as a documented simplification.
- Gameplay balance tuning ("balance, revisit after play"):
  To maintain roughly equal sides and viable tactical choices without one-sided frontal engagements, game values are adjusted:
  1. Tank duel: T-62 115 mm penetration near is set to 290 mm (far 200 mm at 1,600 m); M60A1 front armor is set to 220 mm.
     M60A1 105 mm penetration near is set to 290 mm (far 155 mm at 2,000 m); T-62 front armor is set to 200 mm.
     Frontal cutoff ranges are 1,244 m for T-62 and 1,333 m for M60A1 (both within 1,200 to 1,500 m and within 7.1% of each other).
     Reload times are set equal at 8.5 s. Both penetrate enemy sides and rear at maximum range.
  2. APC balance: KPVT penetration is set to 34 mm near / 18 mm far; M2HB is set to 28 mm near / 14 mm far.
     M113A1 front is 20 mm; BTR-70 and BRDM-2 front is 16 mm. Both penetrate each other frontally inside ~860-875 m,
     and penetrate flank armor at all combat ranges.
  3. Recon contest: M151A2 is armed with 7.62 mm M60 on an M4 pedestal mount (9 mm near, 4 mm far). It penetrates
     BRDM-2 flank armor (8 mm) at close ambush range (< 160 m), while remaining unable to penetrate front armor.
- Turn rates for pivot turns come with the hull heading PR (section 9.5) and are added here then.

## Sources

1. Wikipedia, T-62: https://en.wikipedia.org/wiki/T-62
2. FAS, T-62 technical data: https://man.fas.org/dod-101/sys/land/row/t62tank.htm ; Czech table at
   https://origin.armedconflicts.com/T-62-t116824
3. War Thunder wiki, T-62 (game value, not a period source): https://old-wiki.warthunder.com/T-62
4. Wikipedia, M60 tank: https://en.wikipedia.org/wiki/M60_tank
5. FAS, M60 series: https://man.fas.org/dod-101/sys/land/m60.htm (forward 30 mph, reverse 10 mph)
6. AFV Database, M60: https://afvdatabase.com/usa/m60.html
7. Wikipedia, BTR-70: https://en.wikipedia.org/wiki/BTR-70
8. FAS, BTR-70: https://man.fas.org/dod-101/sys/land/row/btr-70.htm
9. HistoryNet, M113A1: https://historynet.com/arsenal-m113a1-personnel-carrier/
10. Wikipedia, M113: https://en.wikipedia.org/wiki/M113_armored_personnel_carrier
11. FAS, BRDM-2: https://man.fas.org/dod-101/sys/land/row/brdm-2.htm ; Wikipedia:
    https://en.wikipedia.org/wiki/BRDM-2
12. FM 17-12 Tank Gunnery (1977 change pages): https://www.slideshare.net/wolfhag/fm17-12-tankgunnerymar77pt1 ;
    the FM 17-12-2 supplement for the M60A1 is still to be found.
13. TOP 2-2-615A, vehicle detectability: https://everyspec.com/ARMY/Test-Operations-Procedure/TOP-2-2-615A_24FEB2011_52544/
14. Wikipedia, M151: https://en.wikipedia.org/wiki/M151_MUTT
15. US Army TRADOC bulletin on Soviet tanks (DTIC ADA392790): https://apps.dtic.mil/sti/pdfs/ADA392790.pdf (T-62 electric powered traverse full 360 deg in 20.0 s gives 18.0 deg/s)
16. Wikipedia, U-5TS: https://en.wikipedia.org/wiki/U-5TS
17. Warrior Maven, M60 Patton: https://warriormaven.com/land/m60-patton-tank (normal sustained firing rate 6 to 8 rounds per minute, giving 8.5 s reload)
