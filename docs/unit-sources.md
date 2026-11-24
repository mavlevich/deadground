# Unit data sources

Starting values for the historical unit types (TDD section 17.1). Every number in
`content/coldwar/units.json` must come from this table. A value marked **estimate** has no period
source yet; the note says how it was chosen. Estimates are replaced when a better source is found,
and tuned in play only inside the stated range.

Era: about 1976 to 1982. Speeds are given in km/h as in the sources, with m/s for the data file.
Where historical values and current gameplay values differ, both are documented in separate columns.
Gameplay tuning differences are marked "balance, revisit after play".

## Mobility and protection

| Type | Crew + troops | Weight | Road speed | Off-road speed | Reverse | Water | Turn rate | Sourced Armor front / side / rear (mm RHA) | Game Armor front / side / rear (mm RHA) |
|---|---|---|---|---|---|---|---|---|---|
| T-62 (1972 model) | 4 | 37 t [1] | 50 km/h, 13.9 m/s [1] | 30 km/h, 8.3 m/s, estimate [2] | 8 km/h, 2.2 m/s, estimate [3] | no (snorkel only) | 30.0 deg/s, estimate | 200 / 80 / 45 mm [1] [2] [15] | 200 / 80 / 45 mm |
| M60A1 | 4 | 47.7 t [4] | 48 km/h, 13.3 m/s [5] | 25 km/h, 6.9 m/s, estimate [6] | 16 km/h, 4.5 m/s [5] | no | 36.0 deg/s, estimate | 250 / 76 / 41 mm [4] [6] | 220 / 76 / 41 mm |
| BTR-70 | 2 + 8 [7] | 11.5 t [7] | 80 km/h, 22.2 m/s [7] [8] | 40 km/h, 11.1 m/s, estimate | 15 km/h, 4.2 m/s, estimate | 9 km/h [7] | 40.0 deg/s, estimate | 10 / 7 / 7 mm [7] [8] | 16 / 9 / 7 mm |
| M113A1 | 2 + 11 [9] | 12.3 t [10] | 64 km/h, 17.8 m/s [9] | 35 km/h, 9.7 m/s, estimate | 15 km/h, 4.2 m/s, estimate | 5.8 km/h [10] | 36.0 deg/s, estimate | 22 / 15 / 15 mm RHA equiv, estimate [10] | 20 / 12 / 12 mm RHA equiv |
| BRDM-2 | 4 [11] | 7.0 t [11] | 95 km/h, 26.4 m/s [11] | 40 km/h, 11.1 m/s, estimate | 15 km/h, 4.2 m/s, estimate | 10 km/h [11] | 45.0 deg/s, estimate | 14 / 7 / 5 mm [11] | 16 / 8 / 5 mm |
| M151A2 | 1 + 3 | 1.1 t [14] | 106 km/h, 29.4 m/s, estimate | 50 km/h, 13.9 m/s, estimate | 15 km/h, 4.2 m/s, estimate | no | 60.0 deg/s, estimate | none (0 / 0 / 0 mm) [14] | none (0 / 0 / 0 mm) |
| US Rifleman | 1 | 0.08 t, estimate | 15.8 km/h, 4.4 m/s, estimate | 13.0 km/h, 3.6 m/s, estimate | 5.4 km/h, 1.5 m/s, estimate | no | 360.0 deg/s, estimate | none (0 / 0 / 0 mm) | none (0 / 0 / 0 mm) |

## Weapons

| Weapon | Mounted on | Effective range used | Sourced Traverse rate | Game Traverse rate | Sourced Penetration (near / far) | Game Penetration (near / far) | Sourced Reload and Burst | Game Reload and Burst | Notes |
|---|---|---|---|---|---|---|---|---|---|
| 115 mm U-5TS (2A20) smoothbore | T-62 | 1,600 m [2] | 18.0 deg/s [15] | 18.0 deg/s | 240 mm (2,000 m), an estimate in [16]; no checked source for shorter ranges | 290 mm (0 m) / 200 mm (1,600 m) | 40 rounds [2], 1 round per burst, reload ~15 s [15] (or 4 rpm, unverified from secondary sources) | 40 rounds, 1 round per burst, 8.5 s reload | Powered electric traverse: 20.0 s for a full 360 deg turn (TRADOC Bulletin 10 [15]). 3BM-6 steel APFSDS round: [16] gives about 240 mm at 2,000 m against vertical RHA and marks it as an estimate. Reload tuned from historical ~15.0 s to 8.5 s for duel parity; balance, revisit after play. |
| 115 mm, sight | T-62 | | | | | | | | FAS: APFSDS "extremely accurate out to a maximum effective range of 1,600 meters". TSh2B-41 sight with stadiametric ranging; the laser rangefinder came with the T-62M in the 1980s [2]. |
| 105 mm M68 rifled | M60A1 | 2,000 m, estimate [12] | 22.5 deg/s [6] | 22.5 deg/s | ~300 mm (0 m) / ~200 mm (2,000 m), unverified, secondary sources citing Hunnicutt [4] [6] | 290 mm (0 m) / 155 mm (2,000 m) | 63 rounds [4] [6], 1 round per burst, 8.5 s reload [17] | 63 rounds, 1 round per burst, 8.5 s reload | Powered hydraulic traverse: 16.0 s for a full 360 deg turn (AFV Database [6]; TDD 9.5 cites TM 9-2350-215-10). M392A2 APDS round chosen as standard US service round for mid-1970s [4] [6]. Normal sustained rate of 6 to 8 rpm gives 8.5 s reload (Warrior Maven [17]). Penetration tuned to 290/155 mm for frontal duel balance with T-62; balance, revisit after play. |
| 14.5 mm KPVT | BTR-70, BRDM-2 | 1,000 m, game value ([8] gives 2,000 m against ground targets) | 15.0 deg/s, estimate | 15.0 deg/s | 32 mm (500 m) / 20 mm (1,000 m), unverified, no checked source | 34 mm (0 m) / 18 mm (1,000 m) | 500 rounds [8], 5 rounds per burst, 1.5 s interval, estimate | 500 rounds, 5 rounds per burst, 1.5 s interval | BPU-1 turret manual handwheel traverse (estimate: 24.0 s for full 360 deg turn under combat conditions). B-32 API figures of 32 mm at 500 m and 20 mm at 1,000 m are widely quoted but were not found in a checked source. Penetration tuned for APC balance; balance, revisit after play. |
| 12.7 mm M2HB | M113A1 | 1,000 m, estimate | 30.0 deg/s, estimate | 30.0 deg/s | 25.4 mm (200 m) / 13 mm (1,000 m), unverified, no checked source | 28 mm (0 m) / 14 mm (1,000 m) | 2,000 rounds [10], 5 rounds per burst, 1.5 s interval, estimate | 2,000 rounds, 5 rounds per burst, 1.5 s interval | M13 cupola manual traverse (estimate: 12.0 s for full 360 deg turn). .50 cal AP M2 / M8 API figures of 25.4 mm at 200 m and about 13 mm at 1,000 m were not found in a checked source. Penetration tuned for APC balance; balance, revisit after play. |
| 7.62 mm M60 MG | M151A2 | 800 m, estimate | 60.0 deg/s, estimate | 60.0 deg/s | 9.0 mm (0-100 m) / 4.0 mm (800 m), estimate, no checked source | 9.0 mm (0 m) / 4.0 mm (800 m) | 1,000 rounds, 5 rounds per burst, 1.5 s interval, estimate | 1,000 rounds, 5 rounds per burst, 1.5 s interval | Pedestal pintle mount, manual traverse (mount type not verified; estimate: 6.0 s for full 360 deg turn). 7.62 mm NATO M80 Ball / M61 AP penetration is an estimate. Added for armed recon balance; balance, revisit after play. |
| 5.56 mm M16A1 rifle | US Rifleman | 400 m [18] | 0.0 deg/s (instant body aim) | 0.0 deg/s | 3.0 mm (0 m) / 1.0 mm (400 m), estimate | 3.0 mm (0 m) / 1.0 mm (400 m) | 210 rounds [18], 3 rounds per burst, 1.0 s interval | 210 rounds, 3 rounds per burst, 1.0 s interval | Standard US service rifle firing 5.56x45mm M193 ball. Instant body aiming (0.0 deg/s traverse rate). Standard basic combat load: 7 x 30-round magazines (210 rounds). Penetration of mild steel helmet/plate estimated at 3 mm point blank falling to 1 mm at 400 m. Non-penetrating against armored fighting vehicles. |

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
| 5.56 mm M16A1 rifle | US Rifleman | 50: 0.95, 100: 0.85, 200: 0.65, 300: 0.45, 400: 0.25 | 1.5 | 0.15 | 0.05 / 0.15 | Estimate based on qualification standards in FM 23-9 (1974), Table 3-1 [18]; point target qualification marksmanship with iron sights. |

## Placement estimates

These numbers place sensors and targets on the hull or human body. They come from the vehicle heights and widths
above, human anthropometric data, and from our models, not from a period table.

| Type | Eye height | Target points (low, high) | Muzzle height | Radius (half width) |
|---|---|---|---|---|
| T-62 | 2.3 m | 0.9, 2.2 m | 1.75 m | 1.65 m |
| M60A1 | 3.0 m (cupola) | 1.0, 2.9 m | 2.15 m | 1.8 m |
| BTR-70 | 2.0 m | 0.9, 2.0 m | 1.9 m | 1.4 m |
| M113A1 | 2.2 m | 0.8, 2.3 m | 2.1 m | 1.35 m |
| BRDM-2 | 2.0 m | 0.8, 2.1 m | 1.9 m | 1.2 m |
| M151A2 | 1.8 m | 0.6, 1.5 m | 1.4 m | 0.85 m |
| US Rifleman | 1.6 m | 0.6, 1.4 m | 1.35 m | 0.4 m |

Health and damage with armor rules (TDD section 12.3): tanks 100 health, APCs and BRDM-2 50 health,
M151A2 30 health, US Rifleman 15 health. Tank guns deal 50 damage on penetration. Heavy machine guns fire 5-round bursts
dealing 10 damage per penetrating burst. Light machine guns fire 5-round bursts dealing 5 damage per
penetrating burst. Service rifles fire 3-round bursts dealing 5 damage per penetrating burst. Partial bursts scale
damage proportionally to rounds fired. Non-penetrating hits deal zero damage. Hull and turret share armor values in M1
as a documented simplification. Contact tracking in `units.json` (memory, correlation gate) is gameplay tuning for kilometer distances, not period data.

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
| Foot soldier, unmagnified naked eye | 1,000 m |

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
- Turn rates, pivot capability, minimum turning radius, and acceleration (TDD section 9.5):
  T-62: 30.0 deg/s, pivot yes, min radius 0.0 m, accel 2.0 m/s^2 (estimate), decel 4.0 m/s^2 (estimate).
  M60A1: 36.0 deg/s, pivot yes, min radius 0.0 m, accel 2.2 m/s^2 (estimate), decel 4.5 m/s^2 (estimate).
  BTR-70: 40.0 deg/s, pivot no, min radius 9.5 m (estimate, turning circle ~19 m [7]), accel 3.0 m/s^2 (estimate), decel 5.0 m/s^2 (estimate).
  M113A1: 36.0 deg/s, pivot yes, min radius 0.0 m, accel 2.5 m/s^2 (estimate), decel 4.5 m/s^2 (estimate).
  BRDM-2: 45.0 deg/s, pivot no, min radius 7.5 m (estimate, turning circle ~15 m [11]), accel 3.5 m/s^2 (estimate), decel 5.5 m/s^2 (estimate).
  M151A2: 60.0 deg/s, pivot no, min radius 5.5 m (estimate, turning radius ~5.5 m [14]), accel 4.0 m/s^2 (estimate), decel 6.0 m/s^2 (estimate).
  US Rifleman: 360.0 deg/s (human instant turn on foot), pivot yes, min radius 0.0 m, accel 12.0 m/s^2 (sprint burst), decel 16.0 m/s^2.
  Prototype units: prototype_vehicle (pivot yes, min radius 0.0 m, reverse 3.0 m/s, accel 3.0, decel 5.0), recon_vehicle (pivot no, min radius 5.0 m, reverse 3.5 m/s, accel 4.0, decel 6.0), prototype_tank (pivot yes, min radius 0.0 m, reverse 2.5 m/s, accel 2.0, decel 4.0), apc (pivot yes, min radius 0.0 m, reverse 3.2 m/s, accel 2.5, decel 4.5). All values without direct historical sources are gameplay estimates.
- Passenger capacity (TDD section 11.2):
  M113A1: 11 troops (crew 2 + 11 dismounts, sourced from HistoryNet [9] and Wikipedia [10]).
  BTR-70: 7 or 8 troops (crew 2 + 8 dismounts in standard vehicle design sourced from Wikipedia [7] and FAS [8]; 7 dismounts in typical Soviet motorized rifle squad; game value 8).
  Prototype APC: 8 troops (gameplay estimate).
- Tactical smoke systems (TDD section 12.4):
  T-62 (1972 model): onboard TDA (Termicheskaya Dymovaya Apparatura) injects diesel fuel into the hot V-55V exhaust manifold [1] [2] [15].
  Historical figures: roughly 10 L/min fuel burn, laying a 200 to 400 m white screen while moving that persists 2 to 4 minutes depending on wind, with 5 to 15 s residual trail after shutoff.
  Game values (balance, revisit after play until M2 fuel logistics land): `kind = engine_screen`, `charges = 2` bursts, `emit_duration_seconds = 20.0` (lays ~166 m off-road at 8.3 m/s or ~278 m on road at 13.9 m/s), `emit_interval_seconds = 1.0`, `min_cloud_spacing = 10.0 m`, `cloud_radius = 16.0 m` (32 m width, estimate), `cloud_height = 7.0 m` (estimate, covers tank turrets while ridges can see over), `bloom_seconds = 3.0`, `fade_seconds = 15.0`, `cloud_duration_seconds = 105.0` (90 s opaque sustain + 15 s fade, tuned near the 2-minute historical lower bound), `optical_density = 0.25 m^-1`, `cooldown_seconds = 45.0`, `exhaust_offset = 3.5 m`.
  Early M60A1: no smoke dischargers or engine smoke system in this period [4] [6]; `smoke` omitted.
- CH-47D Chinook 3D asset (`src/Game.Godot/Assets/Vehicles/CH47/`, generated by `tools/model-authoring/build_ch47.py` and validated by `tools/model-authoring/test_ch47.py`):
  Procedural Blender Python asset with zero third-party meshes or textures, authored under the repository license.
  Metric scale (`+Y` up, `-Z` forward, landing gear at `Y = 0`) calibrated to Boeing CH-47D / TM 55-1520-240-10 [19]: fuselage length 15.54 m, width over sponsons 3.78 m, overall height to rear rotor hub 5.77 m (front hub 4.72 m), belly clearance 0.72 m, tandem rotor diameter 18.29 m (hub spacing 11.94 m, forward mast tilt 9.0 deg, rear mast tilt 4.0 deg), wheelbase 7.87 m, front dual track 3.20 m, aft track 3.56 m.
  Articulated nodes under root `CH47`: `Fuselage`, `FrontRotor`, `RearRotor`, `FrontRotorBlur`, `RearRotorBlur`, `RearRamp` (rotate local `+X` from 0 deg closed to +42 deg ground loading).
  Measured budgets (6 mesh parts per LOD, all textures and baked `COLOR_0` vertex shading packed inside GLB):
  LOD 0 (`ch47.glb`, close): 9,462 triangles, 20 materials, 1,085,096 bytes.
  LOD 1 (`ch47_lod1.glb`, rts): 6,650 triangles, 20 materials, 906,200 bytes.
  LOD 2 (`ch47_lod2.glb`, distant): 4,374 triangles, 17 materials, 687,088 bytes.
- CH-47D Chinook gameplay parameters (`content/coldwar/units.json`, `ch47d` and `mg_7_62mm_m60d`):
  Flight performance from Boeing CH-47D / US Army TM 55-1520-240-10 [19]: cruise speed ~132 kt (68.0 m/s), nap-of-the-earth (NOE) low-altitude speed ~82 kt (42.0 m/s), climb rate 9.0 m/s, descent rate 7.0 m/s, low altitude band 18.0 m AGL, cruise altitude band 55.0 m AGL, landing clearance radius 10.0 m (rotor radius 9.15 m plus margin), max landing slope 12.0 deg.
  Transport and protection: `passenger_capacity = 24` combat-equipped troops (within the 33-seat cabin and 32-slot engine cap), `max_health = 160` (twin-engine redundant transport airframe, game value), unarmored aluminum skin `armor_front/side/rear = 2.0` mm RHA equivalent (penetrable by 14.5 mm, 12.7 mm, and 7.62 mm MGs at all ranges, and by 5.56 mm rifle fire at close range and low altitude).
  Defensive armament (`mg_7_62mm_m60d`): twin 7.62x51mm M60D door guns, 550 m effective range, 5-round burst, 1,200 rounds total, `can_target_air = true`, `max_air_target_altitude = 65.0` m.
  Cargo and insertion capabilities from US Army TM 55-1520-240-10 and FM 55-450-3 [19]: internal cargo bay accepts infantry (`1` slot each, `100` kg) and light utility vehicles (`m151a2`, `1,100` kg, `8` slots via rear ramp); hover fast-rope insertion (`18.0` m AGL, `0.5` s per trooper) inserts infantry into forests and steep terrain where landing is impossible; external center cargo hook rated to `12,700` kg (28,000 lb) carries `m151a2` (`1,100` kg, ~4% speed penalty) and `m113a1` (`11,200` kg, ~40% speed penalty) on a `10.0` m sling cable, while main battle tanks (`t62` 40,000 kg, `m60a1` 49,000 kg) exceed hook capacity and are rejected.
  Anti-air engagement ceilings (`can_target_air` / `max_air_target_altitude`): `mg_14_5mm_kpvt` (80.0 m), `mg_12_7mm_m2hb` (70.0 m), `mg_7_62mm_m60` (40.0 m), `rifle_5_56mm_m16a1` (25.0 m); main tank cannons (`gun_115mm_u5ts`, `gun_105mm_m68`) remain `can_target_air = false`.
- BM-21 Grad MLRS gameplay parameters (`content/coldwar/units.json`, `bm21` and `rocket_122mm_9m22u`) [20]:
  Ural-375D 6x6 carrier mobility: road speed 75 km/h (20.8 m/s), off-road speed 35 km/h (9.7 m/s, estimate), reverse 3.3 m/s (estimate), combat weight 13,700 kg (exceeds CH-47D sling limit), unarmored cab and launcher (`armor_front/side/rear = 0.0`, `max_health = 40`).
  Armament (`rocket_122mm_9m22u`): 40-tube 122 mm launcher firing 9M22U HE-Frag rockets (6.4 kg explosive warhead) at authentic 0.5 s ripple cadence (full 40-rocket rack in 20 s; each fire mission fires a 20-rocket half-salvo with a 30.0 s inter-salvo reload cooldown). Tactical game range 350 m to 4,000 m (real maximum 20.4 km scaled to tactical map distances), circular dispersion radius 55.0 m (gameplay saturation estimate), HE-Frag splash radius 22.0 m, top/fragmentation penetration 25.0 mm RHA equivalent, damage 90.
- 9K35 Strela-10SV (SA-13 Gopher) air defense and CH-47D M130 flare parameters (`content/coldwar/units.json`, `strela10`, `sam_9m37`, and `ch47d.flares`) [21]:
  MT-LB amphibious tracked carrier mobility: road speed 60 km/h (16.7 m/s), off-road speed 30 km/h (8.3 m/s), reverse 2.5 m/s (estimate), water speed 1.5 m/s (5 to 6 km/h amphibious swim), combat weight 12,100 kg, welded light steel hull armor (`armor_front = 14.0`, `armor_side/rear = 7.0` mm RHA, `max_health = 55`). Electric turret traverse rated up to 100 deg/s in azimuth (`turret_traverse_rate = 70.0` deg/s tactical game value) and -5 to +80 deg canister elevation.
  Armament (`sam_9m37`): 4 ready-to-fire canisters plus 4 hull reload missiles (`ammo_capacity = 8`), infrared homing (`guidance_kind = "infrared"`), `air_targets_only = true`, tactical game range 180 m to 2,200 m (real 800 m to 5,000 m scaled to tactical map distances), `max_air_target_altitude = 350.0` m, 4.0 s inter-shot launch cadence, 145 m/s tactical flight speed (scaled for visual readability and countermeasure reaction window), 11.5 m proximity HE-Frag fuze radius, 120 damage (one hit cripples CH-47D, two hits destroy), `ir_countermeasure_resistance = 0.25`.
  CH-47D M130 infrared flare dispenser (`ch47d.flares`): 8 flare salvo charges, 4.5 s cooldown, 2.6 s active decoy burn window, 70 m decoy radius, 0.78 base IR spoof probability (yields ~58.5% effective decoy rate per burst against 9M37M), auto-flares enabled by default (`default_auto_flares = true`).
- Soviet Series 1-464 5-story precast concrete panel apartment building (`panelka_1_464`, `content/coldwar/units.json` `"buildings"` and `src/Game.Godot/Assets/Buildings/Panelka1464/panelka_1_464.glb`, generated by `tools/model-authoring/build_panelka_1_464.py` and validated by `tools/model-authoring/test_panelka_1_464.py`):
  Procedural asset with zero third-party meshes or textures, authored under the repository license.
  Metric scale (`+Y` up, `+Z` front courtyard entrance facade, `+X` section chaining axis, base at `Y = 0`): 12.0 m x 12.0 m section footprint, 5 floors x 3.1 m = 15.5 m roof height (18.15 m to TV aerial tip), 1.5 m collapsed rubble mound height, 300 max section health, 70 collapse threshold per floor, 12.0 m dust cloud radius, 14.0 m dust cloud height, 9.0 s collapse dust duration.
  Measured GLB budgets (1 unified vertex-colored architectural PBR material, 11 single-draw-call merged module meshes, 1,143,220 bytes):
  `Section_Intact_LOD0` (close): 7,386 triangles (1 mesh); `Section_Intact_LOD1` (RTS, > 180 m): 912 triangles (1 mesh); `Section_Intact_LOD2` (distant, > 420 m): 192 triangles (1 mesh); `Floor_Ground_Intact`: 1,080 triangles (1 mesh); `Floor_Mid_Intact`: 1,512 triangles (1 mesh); `Floor_Breached`: 736 triangles (1 mesh); `Roof_Cap`: 258 triangles (1 mesh); `Rubble_Mound`: 300 triangles (1 mesh); `Debris_Slab_A`, `Debris_Slab_B`, `Debris_Chunk_C`: 12 triangles each (1 mesh each, 256-instance GPU MultiMesh pool).
  Measured 30-building (120-section) runtime budgets (Apple M1):
  Client (`BuildingDestructionRegressionTests`, headless Godot): build 19.13 ms, 1 opaque draw call for all 120 sleeping intact sections (LOD0/LOD1/LOD2 MultiMesh), 1 opaque draw call for all 120 settled rubble sections (Rubble MultiMesh), active 12-section simultaneous collapse frame update P50 0.215 ms / P95 0.220 ms / P99 0.248 ms / max 0.248 ms, 0 B steady-state allocations.
  Server simulation (`tests/Benchmarks --building-destruction`, Release, 120 sections + 16 units + dual BM-21 barrages, 200 ticks): setup 150.96 ms, 60 floors collapsed, P50 0.348 ms / P95 0.526 ms / P99 2.361 ms / max 2.831 ms, 0 B steady-state in unit stress test (176 B peak during 60-floor barrage).

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
18. FM 23-9, M16A1 Rifle and Rifle Marksmanship (1974), Table 3-1 (qualification course standards and target exposures)
19. Boeing CH-47D Chinook / US Army TM 55-1520-240-10 (fuselage length 15.54 m, sponson width 3.78 m, rear rotor height 5.77 m, rotor diameter 18.29 m, mast spacing 11.94 m, wheelbase 7.87 m)
20. FAS / Wikipedia, BM-21 Grad and Ural-375D technical data: https://en.wikipedia.org/wiki/BM-21_Grad (40 x 122 mm tubes, 9M22U 6.4 kg HE-Frag warhead, 0.5 s ripple interval, 75 km/h road speed, 13.7 t combat weight)
21. Wikipedia / FAS, 9K35 Strela-10 (SA-13 Gopher) and MT-LB technical data: https://en.wikipedia.org/wiki/9K35_Strela-10 and https://en.wikipedia.org/wiki/MT-LB (MT-LB 60 km/h road / 30 km/h off-road / 5-6 km/h swim, 7-14 mm steel armor, 4 ready 9M37M canisters + 4 reloads, -5 to +80 deg elevation, 12.1 t combat weight); US Army M130 flare dispenser on CH-47D Chinook.
