# Target games

PadLink is tested against these three games from Gate M2 (PLAN.md 3.8) on, following docs/design.md, "Testing and compatibility plan", Game compatibility. The user picked them at Gate M0 (PLAN.md 1.4) on 2026-10-20.

## Selection rules

From the design:
- Pick 3 target games in M0.
- At least one must be a Steam game that uses the Steam Input API directly, and one must read XInput.
- Each game is tested in both modes with 8 phones. Before each test, confirm that PlayStation Configuration Support is on in Steam.
- Record per game: the players seen in each mode, whether a mid-game switch works, and the recommended mode. These results seed the default per-game profiles (PLAN.md 4.1).

From PLAN.md 3.8: Gate M2 needs both modes to pass with 8 players in at least one target game, so at least one pick must support 8 local players. A game that reads only XInput sees up to 4 pads in Mixed. In All DualShock 4 it sees none, unless Steam Input presents the pads as Xbox pads, and then the cap is 4 again.

## The games

| # | Game | Steam app | Local players | Role | Input path |
| --- | --- | --- | --- | --- | --- |
| 1 | PICO PARK 2 | 2644470 | 2–8 [S1][S2] | Steam Input baseline | Not confirmed. A player report suggests its predecessor, PICO PARK, reads pads only through Steam Input. |
| 2 | Brawlhalla | 291550 | 8 [S3] | Reads XInput | XInput, with a 4-pad cap that players report. It also supports DualShock 4, which may add pads beyond 4 (unconfirmed). |
| 3 | Gang Beasts | 285900 | 8 [S10] | Has an input-API switch | Rewired, reading XInput and DirectInput, with an XInput toggle in its settings |

Rules check: Brawlhalla is the XInput game. PCGamingWiki lists 8 local players for all three [S1][S3][S10], and PICO PARK 2's store page says 2–8 [S2]. PICO PARK 2 is the pick for the Steam Input API rule, but no source confirms yet that it uses the Steam Input API (see its first caveat).

Most caveats below come from Steam forum posts by players, some years old. They are leads for the M2 tests (3.8), not facts about the current builds.

### 1. PICO PARK 2

TECOPARK and Gemdrops, Steam app 2644470, released on Steam on 12 Sep 2024, for 2–8 players [S1][S2]. This is not the 2021 PICO PARK (app 1509960) or the Classic Edition, which are separate Steam apps.

**Why:** the Steam Input baseline: a Steam game that uses the Steam Input API directly, to show what a game sees when Steam handles the pads.

**Caveats**
- **Its Steam Input API use is unconfirmed.** PCGamingWiki lists every input field for PICO PARK 2 as unknown, including Steam Input API support [S1]. The store page names no controller support [S2], and no PICO PARK 2 thread found mentions Steam Input. **Check before Gate M2 (👤), at the latest when 3.8 starts:**
  1. With Steam Input on (Steam → the game's Properties → Controller), open the game's controller layout in Steam. A game that uses the Steam Input API lists its own in-game actions there, such as jump or move. A layout of only gamepad buttons or keyboard keys means it doesn't use the API.
  2. Turn Steam Input off for the game and start it with an Xbox controller. If that pad works, the game also reads XInput itself. A DualShock 4 isn't a fair test here, because a game that reads only XInput ignores it too.

  If step 1 shows no in-game actions, the Steam Input API rule needs a different game.
- **A player report suggests the predecessor reads pads only through Steam Input.** These caveats are about the 2021 PICO PARK, from the same developer, and may not carry over:
  - A player who forced Steam Input off found "nothing works at all" [S4].
  - With Steam Input at its default, one player's controllers all "map to the same default "wasd" template" and moved one character [S4]. The developer's fix, in 2021 and 2024, was to apply the official layout in Steam's Controller Layout screen [S4][S5]. Before the M2 test, check the layout Steam applies to the virtual Xbox 360 and DualShock 4 pads.
  - The developer: controllers "are assigned to each character in the order of connection" [S4]. If PICO PARK 2 does the same, players match PadLink slots only if the pads connect in slot order.
- **The keyboard takes player 1 or 2 by default.** The developer: "The keyboard is fixed to 1P or 2P by default, so you can control the same character as the controller", and it can be changed under Keyboard Config on the Option screen (Jan 2025) [S11]. During tests, keep the PC's keyboard from sharing a character with a pad.
- **8 players.** One player on PICO PARK 2's forum reported "8 player is working well" (Oct 2025) [S12].
- **The developer's own phone-pad app.** The store page says a smartphone can be used as a gamepad with the TecoGamePad app [S2].

### 2. Brawlhalla

**Why:** the XInput game. Players report that it reads Xbox pads through XInput, which caps those at 4.

**Caveats**
- **Players report a 4-pad cap.** A 2016 post quotes the developer: "By default Xinput only allows 4 controllers but there are a couple of different approaches around that", and "8 player local works as long as you can get the controllers to register on your PC" [S6]. Players in the same thread found it recognises at most 4 pads and has no XInput/DirectInput switch [S6]. It also reads DualShock 4 pads [S3]; whether they add players beyond 4 is unconfirmed.
- **Steam Input makes pads look like Xbox pads.** A 2024 post in the same thread says to disable Steam Input "because Steam Input makes your controllers appear as XBox controllers to the game" [S6]. With Steam Input on, the DualShock 4 pads may count against the same 4. That clashes with the design's rule to have PlayStation Configuration Support on before each test (see [For Gate M2](#for-gate-m2-38)).
- **Pads of the same type.** The same 2024 post says "Brawlhalla has issues with controllers of the same type" and advises "Max 1 of each type and not XBox controllers" [S6]. If that is true, it affects both modes: All DualShock 4 creates 8 pads of one type, and Mixed creates 4 Xbox pads and 4 DualShock 4 pads. One player reported this, and nobody has confirmed it.
- **8 players must be switched on:** "Couch Party" → Settings → Max Players 8 (2016 post) [S6].
- **PCGamingWiki:** XInput and DualShock 4 controllers work, hotplug works, generic controllers don't ("Switch Controller not detected"), and the controller-support note says "X360CE recommended" [S3].

### 3. Gang Beasts

**Why:** the game with an input-API switch. It reads both XInput and DirectInput through Rewired and has an XInput toggle, so the same game can be tested both ways.

**Caveats**
- **The toggle's label.** The user knows it as "Use XInput". The forum posts only call it "XInput" ("toggling XInput in the options") [S9]. Confirm the label at 3.8.
- **Players report a cap of 4 with XInput on.** They report a limit of 4 with XInput on and more than 4 with it off (2017–2019 [S7], 2018 [S8]). The reports conflict: some found that their controllers stopped working with XInput off [S7][S8], and one needed it on to get 5 players [S8].
- **Steam's configuration support.** Some players turned off Steam's controller configuration support, or changed their Steam Big Picture configuration, to get past 4 controllers [S7][S8]. That clashes with the design's PlayStation Configuration Support rule, as for Brawlhalla.
- **Rewired.** FAQ text posted on the Steam forum in 2020 says "Gang Beasts uses the Rewired framework for controller support on Windows, Mac OS X, and Linux" [S9]. The poster isn't marked as a developer.
- **PCGamingWiki:** controller support is "XInput and DirectInput", and DualShock 4 works (V1, wired and Bluetooth) [S10].
- **Doubled or missing players in Mixed.** It reads both XInput and DirectInput. The design lists a game that reads both APIs badly in Mixed mode as a risk, with doubled or missing players as the effect (docs/design.md, "Risks and mitigations").

## For Gate M2 (3.8)

- For Brawlhalla and Gang Beasts, the design's rule (PlayStation Configuration Support on) may hide pads. Run each mode with that rule first. If fewer than 8 players appear, run a short diagnostic with Steam Input off for that game, which counts players and is not another 60-minute session. Record both results. Only the runs that follow the design's rule count toward Gate M2.
- For Gang Beasts, run each mode with XInput on and with it off. In Mixed, watch for doubled or missing players.
- For PICO PARK 2, run the Steam Input check above first if it hasn't been done, then check the layout Steam applies to both virtual pad types.

## Sources

Retrieved 2026-10-20. PCGamingWiki pages were read through its API (`/w/api.php?action=parse&prop=wikitext`), because the pages themselves block automated requests.

- [S1] PCGamingWiki, PICO PARK 2: https://www.pcgamingwiki.com/wiki/Pico_Park_2
- [S2] Steam store, PICO PARK 2: https://store.steampowered.com/app/2644470/PICO_PARK_2/
- [S3] PCGamingWiki, Brawlhalla: https://www.pcgamingwiki.com/wiki/Brawlhalla
- [S4] Steam, PICO PARK (2021) discussion, Sep 2021 (developer replies): https://steamcommunity.com/app/1509960/discussions/0/3033726413399263644
- [S5] Steam, PICO PARK (2021) discussion, Mar–May 2024 (developer replies): https://steamcommunity.com/app/1509960/discussions/0/6554498922765771894
- [S6] Steam group "More than 4 local multiplayer", Brawlhalla thread, 2016–2024: https://steamcommunity.com/groups/morethan4localmultiplayer/discussions/0/154644928864698895
- [S7] Steam, Gang Beasts discussion, 2017–2019: https://steamcommunity.com/app/285900/discussions/0/142261027576603101
- [S8] Steam, Gang Beasts discussion, Jul 2018: https://steamcommunity.com/app/285900/discussions/0/1710690176746245032
- [S9] Steam, Gang Beasts discussion, 2020 (FAQ text): https://steamcommunity.com/app/285900/discussions/0/2796125475577524072
- [S10] PCGamingWiki, Gang Beasts: https://www.pcgamingwiki.com/wiki/Gang_Beasts
- [S11] Steam, PICO PARK 2 discussion "Remote Play Together issue", Jan 2025 (developer reply): https://steamcommunity.com/app/2644470/discussions/0/600767415367764720/
- [S12] Steam, PICO PARK 2 discussion "Pico Park 3 | 16 players?", 2025: https://steamcommunity.com/app/2644470/discussions/0/591777966325826727/
