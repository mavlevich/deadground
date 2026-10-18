# Unit data sources

Starting values for the historical unit types (TDD section 17.1). Every number in
`content/coldwar/units.json` must come from this table. A value marked **estimate** has no period
source yet; the note says how it was chosen. Estimates are replaced when a better source is found,
and tuned in play only inside the stated range.

Era: about 1976 to 1982. Speeds are given in km/h as in the sources, with m/s for the data file.

## Mobility and protection

| Type | Crew + troops | Weight | Road speed | Off-road speed | Reverse | Water | Armor (front) |
|---|---|---|---|---|---|---|---|
| T-62 (1972 model) | 4 | 37 t [1] | 50 km/h, 13.9 m/s [1] | 30 km/h, 8.3 m/s, estimate [2] | 8 km/h, 2.2 m/s, estimate [3] | no (snorkel only) | hull 102 mm at 60 deg, turret 242 mm cast (214 mm before 1972) [1] |
| M60A1 | 4 | 47.7 t [4] | 48 km/h, 13.3 m/s [5] | 25 km/h, 6.9 m/s, estimate [6] | 16 km/h, 4.5 m/s [5] | no | hull 109 mm at 65 deg, turret 250 mm [4] |
| BTR-70 | 2 + 8 [7] | 11.5 t [7] | 80 km/h, 22.2 m/s [7] [8] | 40 km/h, 11.1 m/s, estimate | 15 km/h, 4.2 m/s, estimate | 9 km/h [7] | 9 to 10 mm, sides 7 mm [7] [8] |
| M113A1 | 2 + 11 [9] | 12.3 t [10] | 64 km/h, 17.8 m/s [9] | 35 km/h, 9.7 m/s, estimate | 15 km/h, 4.2 m/s, estimate | 5.8 km/h [10] | aluminum 28 to 44 mm [10] |
| BRDM-2 | 4 [11] | 7.0 t [11] | 95 km/h, 26.4 m/s [11] | 40 km/h, 11.1 m/s, estimate | 15 km/h, 4.2 m/s, estimate | 10 km/h [11] | up to 14 mm [11] |
| M151A2 | 1 + 3 | 1.1 t [14] | 106 km/h, 29.4 m/s, estimate | 50 km/h, 13.9 m/s, estimate | 15 km/h, 4.2 m/s, estimate | no | none |

## Weapons

| Weapon | Mounted on | Effective range used | Notes |
|---|---|---|---|
| 115 mm U-5TS (2A20) smoothbore | T-62 | 1,600 m [2] | 40 rounds [2] [15]. One round per 15 s: a US Army bulletin gives 3 to 4 rounds per minute in practice [15]; the gun is often quoted at 6 to 10 [16]. |
| 115 mm, sight | T-62 | | FAS: APFSDS "extremely accurate out to a maximum effective range of 1,600 meters". TSh2B-41 sight with stadiametric ranging; the laser rangefinder came with the T-62M in the 1980s [2]. |
| 105 mm M68 rifled | M60A1 | 2,000 m, estimate [12] | 63 rounds [5]. One round per 8.5 s, about 7 rounds per minute [17]. M17A1 coincidence rangefinder in the M19 fire control system [4]. One undocumented hit curve gives about 50% first-round hits at 1,500 m and 28% at 2,000 m; it belongs in section 12.3, not here. |
| 14.5 mm KPVT, 7.62 mm PKT | BTR-70, BRDM-2 | 1,000 m against light armor, estimate | Ammunition 500 + 2,000 rounds on the BTR-70 [8]. |
| 12.7 mm M2HB | M113A1 | 1,000 m against light armor, estimate | |

## Placement estimates

These numbers place sensors and targets on the hull. They come from the vehicle heights and widths
above and from our models, not from a period table.

| Type | Eye height | Target points (low, high) | Muzzle height | Radius (half width) |
|---|---|---|---|---|
| T-62 | 2.3 m | 0.9, 2.2 m | 1.75 m | 1.65 m |
| M60A1 | 3.0 m (cupola) | 1.0, 2.9 m | 2.15 m | 1.8 m |
| BTR-70 | 2.0 m | 0.9, 2.0 m | | 1.4 m |
| M113A1 | 2.2 m | 0.8, 2.3 m | | 1.35 m |
| BRDM-2 | 2.0 m | 0.8, 2.1 m | | 1.2 m |
| M151A2 | 1.8 m | 0.6, 1.5 m | | 0.85 m |

Health stays a placeholder until armor and penetration land (TDD section 12.3): tanks 100, APCs and
the BRDM-2 50, the M151A2 30, and every gun hit does 50. APCs and scout vehicles carry no weapon yet.
Contact tracking in `units.json` (memory, correlation gate) is gameplay tuning for kilometer
distances, not period data.

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

## Notes

- [2] The Czech technical table gives 30 km/h cross-country; Wikipedia gives 40 km/h [1]. The
  lower value is used because it describes sustained movement, not a peak.
- [3] One reverse gear. The only figure found (8 km/h) is from a game wiki, so it stays an estimate.
- M151A2: about 106 km/h (66 mph) is the commonly quoted top speed, but no primary source was found.
- [6] Wikipedia and secondary sources give 19 km/h cross-country for the M60A1 [4]. 25 km/h is used
  so that the T-62 and M60A1 keep their road speed ratio off-road; to confirm from the TM.
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
15. US Army TRADOC bulletin on Soviet tanks (DTIC): https://apps.dtic.mil/sti/pdfs/ADA392790.pdf
16. Wikipedia, U-5TS: https://en.wikipedia.org/wiki/U-5TS
17. Warrior Maven, M60 Patton: https://warriormaven.com/land/m60-patton-tank
