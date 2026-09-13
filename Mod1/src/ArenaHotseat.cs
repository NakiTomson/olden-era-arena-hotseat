using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using BepInEx.Unity.IL2CPP.Hook;

namespace PersonalMods.OldenEra;

// Version-locked native IL2CPP adapter. No game DLL is patched on disk.
// Every RVA and field offset below belongs ONLY to Steam build 25061458.
[BepInPlugin("personal.oldenera.arena-hotseat", "Arena Hotseat", "0.2.1")]
public sealed class ArenaHotseat : BasePlugin
{
    const string SupportedHash = "bb4ea1f5b28af6b3f21031bb53f7fa50021579a8c74ade2f05904696d6fdb38a";
    readonly List<INativeDetour> hooks = new();
    IntPtr module, logic, controller, readyScreen;
    IntPtr logicHandle, controllerHandle;
    IntPtr ownedPlayer, originalOwnedSides, replacementOwnedSides, ownedPlayerHandle, originalSidesHandle;
    bool pendingHandoff, handedOff, failed;
    string player1 = "Игрок 1", player2 = "Игрок 2";
    InitLogic originalInit = null!;
    InitView originalViewInit = null!;
    Unary originalUpdate = null!, originalDestroy = null!, originalReady = null!;
    ConvertSide originalConvert = null!;
    OwnsSide originalOwnsSide = null!;
    SetPlayerToSlot originalSetPlayerToSlot = null!;
    InitView originalProfileInit = null!, bindUnits = null!, bindMagicPreviews = null!;
    IntPtr displayedProfileHero;
    Unary closeDraft = null!;
    InitView showHeroChoice = null!;
    GetObject getGameObject = null!;
    SetActive setActive = null!;
    GetSingleton getScreenManager = null!;
    InitView hideScreen = null!;
    ShowScreen showExistingScreen = null!;
    AnimateOut hideProgressAnimation = null!;
    SetStage setProgressStage = null!;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void InitLogic(IntPtr self, IntPtr info, IntPtr random, IntPtr method);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void InitView(IntPtr self, IntPtr argument, IntPtr method);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void Unary(IntPtr self, IntPtr method);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr GetObject(IntPtr self, IntPtr method);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr GetSingleton(IntPtr method);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr ShowScreen(IntPtr manager, IntPtr screen, IntPtr method);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void SetActive(IntPtr self, [MarshalAs(UnmanagedType.I1)] bool active, IntPtr method);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void AnimateOut(IntPtr self, [MarshalAs(UnmanagedType.I1)] bool instant, IntPtr method);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void SetStage(IntPtr self, int stage, IntPtr method);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr ConvertSide(IntPtr self, IntPtr side, [MarshalAs(UnmanagedType.I1)] bool isLeft, IntPtr method);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)] delegate bool OwnsSide(IntPtr self, int player, int side, IntPtr method);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)] delegate bool SetPlayerToSlot(IntPtr self, int slotIndex, IntPtr playerId,
        [MarshalAs(UnmanagedType.I1)] bool isLocalLobby, int gameMode, IntPtr method);

    public override void Load()
    {
        if (!Config.Bind("General", "Enabled", true, "Allow two human players in local Arena; respect the lobby's Player / AI selection.").Value) return;
        player1 = Config.Bind("Players", "Player1", "Игрок 1", "First local player name.").Value;
        player2 = Config.Bind("Players", "Player2", "Игрок 2", "Second local player name.").Value;
        using var input = File.OpenRead(Path.Combine(Paths.GameRootPath, "GameAssembly.dll"));
        using var sha = SHA256.Create();
        var hash = BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "").ToLowerInvariant();
        if (hash != SupportedHash)
        {
            Log.LogError("UNSUPPORTED BUILD. Arena Hotseat disabled before installing any hook. Update the mod for this game build.");
            return;
        }
        module = Native.GetModuleHandle("GameAssembly.dll");
        if (module == IntPtr.Zero) throw new InvalidOperationException("GameAssembly.dll was not loaded.");

        // Validate every entry point before installing any hook; protects against another patcher.
        Check(0x244BA60, "48895C240848896C2418"); // fui.Init(Arena.StartInfo, SerializableRandom)
        Check(0x243C180, "48895C2408574883EC20"); // ftq.Init(fui)
        Check(0x2429450, "4883EC28"); // BhArena.Update
        Check(0x2429240, "40534883EC30"); // BhArena.OnDestroy
        Check(0x2447CE0, "4883EC28488B8998000000"); // ScArenaGame.blpv: ready button
        Check(0x244E230, "448844241848894C2408"); // ful.blsv: create TransferSide
        Check(0x27EFB70, "4883EC28458BD085D2"); // bfw.lle: Player.sides ownership lookup
        Check(0x2C3D260, "4883EC28803D"); // cmy.tbv: existing screen manager
        Check(0x2C3CC90, "40534883EC204533C0"); // cmy.tch: close a registered screen
        Check(0x2C3D830, "48895C2408574883EC20"); // cmy.tcd: show an already configured screen
        Check(0x1E551D0, "48895C24185741564157"); // Slots.SetPlayerToSlot
        Check(0x1E5528E, "84DB740E817C2460E8030000"); // only LB_Classic skips moving the existing local player
        Check(0xA9A870, "48895C24084889742410"); // ScArenaProfile.iys<object>
        Check(0x2DA0700, "48895C24184889742420"); // BhUnitSlots.Init(dup)
        Check(0x2444CB0, "48895C24184889542410"); // BhArenaMagicPreviewsController.blqc(List<dtd>)
        Check(0x2AA0F50, "48895C2418488974242089542410"); // BhArenaLine.hmg(int)
        Check(0x3A8CCE0, "48895C2408574883EC600FB6FA"); // UiAnimatorPrime.Out(bool isInsta)

        closeDraft = Function<Unary>(0x243D870);
        showHeroChoice = Function<InitView>(0x243DA30);
        getGameObject = Function<GetObject>(0x404B4C0);
        setActive = Function<SetActive>(0x404EA50);
        getScreenManager = Function<GetSingleton>(0x2C3D260);
        hideScreen = Function<InitView>(0x2C3CC90);
        showExistingScreen = Function<ShowScreen>(0x2C3D830);
        bindUnits = Function<InitView>(0x2DA0700);
        bindMagicPreviews = Function<InitView>(0x2444CB0);
        hideProgressAnimation = Function<AnimateOut>(0x3A8CCE0);
        setProgressStage = Function<SetStage>(0x2AA0F50);
        try
        {
            hooks.Add(INativeDetour.CreateAndApply(Address(0x244BA60), new InitLogic(OnInit), out originalInit));
            hooks.Add(INativeDetour.CreateAndApply(Address(0x243C180), new InitView(OnViewInit), out originalViewInit));
            hooks.Add(INativeDetour.CreateAndApply(Address(0x2429450), new Unary(OnUpdate), out originalUpdate));
            hooks.Add(INativeDetour.CreateAndApply(Address(0x2429240), new Unary(OnDestroy), out originalDestroy));
            hooks.Add(INativeDetour.CreateAndApply(Address(0x2447CE0), new Unary(OnReady), out originalReady));
            hooks.Add(INativeDetour.CreateAndApply(Address(0x244E230), new ConvertSide(OnConvertSide), out originalConvert));
            hooks.Add(INativeDetour.CreateAndApply(Address(0x27EFB70), new OwnsSide(OnOwnsSide), out originalOwnsSide));
            hooks.Add(INativeDetour.CreateAndApply(Address(0x1E551D0), new SetPlayerToSlot(OnSetPlayerToSlot), out originalSetPlayerToSlot));
            hooks.Add(INativeDetour.CreateAndApply(Address(0xA9A870), new InitView(OnProfileInit), out originalProfileInit));
            Log.LogInfo("READY: Arena Hotseat 0.2.1; Steam build 25061458; 9 hooks installed. Local Arena follows the configured slots.");
        }
        catch
        {
            for (int i = hooks.Count - 1; i >= 0; i--) hooks[i].Dispose();
            hooks.Clear();
            throw;
        }
    }

    bool OnSetPlayerToSlot(IntPtr self, int slotIndex, IntPtr playerId, bool isLocalLobby, int gameMode, IntPtr method)
    {
        // In this build the ONLY use of gameMode in SetPlayerToSlot is the
        // multi-slot ownership guard at 0x1E55292. Reuse vanilla LB_Classic's
        // local ownership rule for Arena, without changing LobbyState.GameMode.
        // Index validation, assignment and slot-change notifications stay vanilla.
        bool localArena = isLocalLobby && (gameMode == 1 || gameMode == 1001);
        bool result = originalSetPlayerToSlot(self, slotIndex, playerId, isLocalLobby,
            localArena ? 1000 : gameMode, method);
        if (localArena && result)
            Log.LogInfo("LOBBY PLAYER: slot " + slotIndex + " joined locally; existing human slots preserved.");
        return result;
    }

    void OnInit(IntPtr self, IntPtr info, IntPtr random, IntPtr method)
    {
        ResetSession();
        bool local = info != IntPtr.Zero && ReadInt(info, 0x18) == 0; // EPlay.Local
        if (local)
        {
            try { ConfigureLocalArena(self, info); }
            catch (Exception e) { Fail("Local arena setup", e); }
        }
        originalInit(self, info, random, method);
        if (local && !failed && self == logic)
        {
            // One shared preparation timer cannot fairly cover two sequential drafts.
            IntPtr timer = ReadPtr(self, 0x40);
            if (timer != IntPtr.Zero) Marshal.WriteByte(timer, 0x14, 0);
        }
    }

    void ConfigureLocalArena(IntPtr self, IntPtr info)
    {
        IntPtr sides = Required(ReadPtr(info, 0x20));
        if (ReadInt(sides, 0x18) != 2) throw new InvalidOperationException("Arena must have exactly two sides.");
        IntPtr left = Required(ReadPtr(sides, 0x20)), right = Required(ReadPtr(sides, 0x28));
        int leftType = ReadInt(left, 0x18), rightType = ReadInt(right, 0x18);
        if (leftType != 0 || rightType != 0) // SideInfo.EType.Player
        {
            Log.LogInfo("LOCAL ARENA: configured side types [" + leftType + "," + rightType + "]; vanilla opponent control retained.");
            return;
        }
        IntPtr mySides = Required(ReadPtr(info, 0x28));
        IntPtr localSides = Required(Native.il2cpp_array_new_specific(Native.il2cpp_object_get_class(mySides), (UIntPtr)2));
        IntPtr arrayHandle = Native.il2cpp_gchandle_new(localSides, false);
        try
        {
            Marshal.WriteInt32(localSides, 0x20, 0);
            Marshal.WriteInt32(localSides, 0x24, 1);
            // Set all references before the original Init creates phases and AI controllers.
            WriteRef(info, 0x28, localSides);
            WriteRef(left, 0x10, Native.il2cpp_string_new(player1));
            WriteRef(right, 0x10, Native.il2cpp_string_new(player2));
            logic = self;
            logicHandle = Native.il2cpp_gchandle_new(self, false);
            Log.LogInfo("LOCAL ARENA: two human sides; mySides=[0,1]. Draft starts with player 1.");
        }
        finally { Native.il2cpp_gchandle_free(arrayHandle); }
    }

    void OnProfileInit(IntPtr self, IntPtr argument, IntPtr method)
    {
        if (!failed && logic != IntPtr.Zero && controller != IntPtr.Zero && argument != IntPtr.Zero)
        {
            try
            {
                // This generic specialization receives ftv (fsa + profile stage).
                // Never reinterpret an unrelated profile argument or another arena.
                string? argumentType = Marshal.PtrToStringAnsi(Native.il2cpp_class_get_name(Native.il2cpp_object_get_class(argument)));
                IntPtr side = ReadPtr(controller, 0x18);
                if (argumentType == "ftv" && side != IntPtr.Zero && ReadPtr(side, 0x10) == logic)
                {
                    IntPtr hero = ReadPtr(side, 0x40);
                    if (hero != IntPtr.Zero && ReadPtr(argument, 0x10) == hero)
                    {
                        IntPtr profile = Required(ReadPtr(self, 0xA0));
                        IntPtr party = Required(ReadPtr(hero, 0x20));
                        // BhHeroProfileWindow.blfl skips Init when the party is
                        // empty. Explicit binding removes the old party listener
                        // and clears the previous hero's unit icons even in that case.
                        bindUnits(Required(ReadPtr(profile, 0x98)), party, IntPtr.Zero);
                        IntPtr previews = Required(ReadPtr(Required(ReadPtr(self, 0xA8)), 0xA0));
                        IntPtr magics = Required(ReadPtr(Required(ReadPtr(hero, 0x58)), 0x18));
                        // Rebuild the shared preview before vanilla applies the
                        // stage's covers. PickMagic later adds its current choices.
                        bindMagicPreviews(previews, magics, IntPtr.Zero);
                        if (displayedProfileHero != hero)
                        {
                            displayedProfileHero = hero;
                            Log.LogInfo("DRAFT PROFILE: side " + Marshal.ReadByte(side, 0x18) +
                                "; army rebound to current hero; learned magic count=" + ReadInt(magics, 0x18) + ".");
                        }
                    }
                }
            }
            catch (Exception e) { Fail("Draft profile binding", e); }
        }
        originalProfileInit(self, argument, method);
    }

    void OnViewInit(IntPtr self, IntPtr targetLogic, IntPtr method)
    {
        originalViewInit(self, targetLogic, method);
        if (failed || targetLogic != logic || logic == IntPtr.Zero) return;
        controller = self;
        if (controllerHandle != IntPtr.Zero) Native.il2cpp_gchandle_free(controllerHandle);
        controllerHandle = Native.il2cpp_gchandle_new(self, false);
        Log.LogInfo("DRAFT VIEW: player 1 attached.");
    }

    void OnReady(IntPtr self, IntPtr method)
    {
        // This build also contains alternate view initializers. The live screen is the
        // authoritative owner; capture its controller before submitting the command.
        if (!failed && logic != IntPtr.Zero)
        {
            IntPtr liveController = ReadPtr(self, 0xD8);
            if (liveController != IntPtr.Zero && ReadPtr(liveController, 0x10) == logic && liveController != controller)
            {
                if (controllerHandle != IntPtr.Zero) Native.il2cpp_gchandle_free(controllerHandle);
                controller = liveController;
                controllerHandle = Native.il2cpp_gchandle_new(controller, false);
                Log.LogInfo("DRAFT VIEW: captured the live screen controller.");
            }
        }
        // Submit the vanilla ready command first. Wait for processing in BhArena.Update.
        originalReady(self, method);
        if (!failed && !handedOff && logic != IntPtr.Zero && ReadPtr(self, 0xD8) == controller)
        {
            pendingHandoff = true;
            readyScreen = self;
            Log.LogInfo("READY COMMAND: waiting for player 1 to enter WaitStartBattle.");
        }
    }

    void OnUpdate(IntPtr self, IntPtr method)
    {
        originalUpdate(self, method);
        if (failed || !pendingHandoff || controller == IntPtr.Zero || logic == IntPtr.Zero) return;
        if (Marshal.PtrToStringAnsi(Native.il2cpp_class_get_name(Native.il2cpp_object_get_class(self))) != "BhArena") return;
        // This component owns the current arena logic. Never use a stale previous-session pointer.
        if (ReadPtr(self, 0x50) != logic) return;
        try
        {
            IntPtr registry = Required(ReadPtr(logic, 0x38));
            IntPtr sides = Required(ReadPtr(registry, 0x10));
            if (ReadInt(sides, 0x18) != 2) throw new InvalidOperationException("Unexpected arena side count.");
            IntPtr first = Required(ReadPtr(sides, 0x20));
            IntPtr second = Required(ReadPtr(sides, 0x28));
            IntPtr firstPhase = Required(ReadPtr(Required(ReadPtr(first, 0x30)), 0x18));
            if (GetPhase(firstPhase) != 9) return; // EPhase.WaitStartBattle, command not yet processed otherwise.
            IntPtr secondPhase = Required(ReadPtr(Required(ReadPtr(second, 0x30)), 0x18));
            if (GetPhase(secondPhase) != 4) throw new InvalidOperationException("Second player's draft already progressed unexpectedly.");
            pendingHandoff = false;
            handedOff = true;
            closeDraft(controller, IntPtr.Zero);
            // The normal draft-close list omits ScWaitBattle. Player 1's ready
            // phase opened it, so it otherwise covers every non-level-up choice
            // for player 2 while those hidden controls can still accept clicks.
            CloseWaitingScreen();
            WriteRef(registry, 0x18, second);
            WriteRef(controller, 0x18, second);
            IntPtr keyboard = ReadPtr(logic, 0x88);
            if (keyboard != IntPtr.Zero) WriteRef(keyboard, 0x18, second);
            // Hide the old ready button. Vanilla phase transitions show it again after player 2's draft.
            IntPtr screen = Required(readyScreen);
            // WaitStartBattle also hid ScArenaGame through cmy.tcg. Restore its
            // existing instance (without reinitializing the controller or picks).
            // Its OnHide explicitly disables the background object separately.
            Required(showExistingScreen(Required(getScreenManager(IntPtr.Zero)), screen, IntPtr.Zero));
            setActive(Required(ReadPtr(screen, 0xB0)), true, IntPtr.Zero);
            IntPtr button = ReadPtr(screen, 0xA8);
            if (button != IntPtr.Zero) setActive(getGameObject(button, IntPtr.Zero), false, IntPtr.Zero);
            // A cosmetic reset failure must not stop the second player's draft or battle control.
            try { ResetPreparationProgress(screen); }
            catch (Exception e) { Log.LogError("DRAFT PROGRESS RESET FAILED: " + e + ". Hotseat handoff continues."); }
            // Replay only the VIEW event. Do not enter/restart the logic phase or reroll its offers.
            showHeroChoice(controller, secondPhase, IntPtr.Zero);
            Log.LogInfo("HANDOFF COMPLETE: control switched to player 2; hero-choice view requested; player 1 remains ready.");
        }
        catch (Exception e) { Fail("Draft handoff", e); }
    }

    void ResetPreparationProgress(IntPtr screen)
    {
        IntPtr line = Required(ReadPtr(screen, 0xA0)); // ScArenaGame.line
        IntPtr[] stages = ReadProgressReferences(Required(ReadPtr(line, 0x60)));
        IntPtr[] segments = ReadProgressReferences(Required(ReadPtr(line, 0x58)));
        if (stages.Length != 7 || segments.Length < stages.Length - 1)
            throw new InvalidOperationException("Unexpected preparation progress layout.");

        // Resolve the complete UI structure before changing it. Start() must not
        // be called again: it appends the seven stages to the existing list.
        var animations = new List<IntPtr>(segments);
        var highlights = new IntPtr[stages.Length];
        for (int i = 0; i < stages.Length; i++)
        {
            highlights[i] = Required(ReadPtr(stages[i], 0x38)); // BhArenaLineItem.active
            animations.AddRange(ReadProgressReferences(Required(ReadPtr(stages[i], 0x20))));
        }
        foreach (IntPtr stage in stages)
        {
            Marshal.WriteByte(stage, 0x40, 0); // stop the previous active stage's ring pulses
            Marshal.WriteInt32(stage, 0x44, 0); // ring index
            Marshal.WriteInt32(stage, 0x48, 0); // elapsed pulse time: float 0.0
        }
        foreach (IntPtr animation in animations)
            hideProgressAnimation(animation, true, IntPtr.Zero);
        foreach (IntPtr highlight in highlights)
            setActive(highlight, false, IntPtr.Zero);

        // hmg only turns on the requested stage/preceding segment; it does not
        // clear completed visuals. Invalidate its cached index and activate Hero
        // once, then normal phase notifications continue the second player's bar.
        Marshal.WriteInt32(line, 0x68, -1);
        setProgressStage(line, 0, IntPtr.Zero);
        Log.LogInfo("DRAFT PROGRESS: player 2 reset to Hero; completed stages and segments cleared.");
    }

    static IntPtr[] ReadProgressReferences(IntPtr list)
    {
        int count = ReadInt(list, 0x18);
        if (count < 0 || count > 32) throw new InvalidOperationException("Invalid progress reference count.");
        if (count == 0) return Array.Empty<IntPtr>();
        IntPtr array = Required(ReadPtr(list, 0x10));
        if (ReadInt(array, 0x18) < count) throw new InvalidOperationException("Invalid progress reference array.");
        var items = new IntPtr[count];
        for (int i = 0; i < count; i++) items[i] = Required(ReadPtr(array, 0x20 + i * IntPtr.Size));
        return items;
    }

    void CloseWaitingScreen()
    {
        IntPtr manager = Required(getScreenManager(IntPtr.Zero));
        IntPtr name = Required(Native.il2cpp_string_new("ScWaitBattle"));
        IntPtr nameHandle = Native.il2cpp_gchandle_new(name, false);
        try { hideScreen(manager, name, IntPtr.Zero); }
        finally { Native.il2cpp_gchandle_free(nameHandle); }
    }

    IntPtr OnConvertSide(IntPtr self, IntPtr side, bool isLeft, IntPtr method)
    {
        IntPtr result = originalConvert(self, side, isLeft, method);
        if (failed || logic == IntPtr.Zero || side == IntPtr.Zero || ReadPtr(side, 0x10) != logic || result == IntPtr.Zero) return result;
        // Vanilla marks every non-active human REMOTE, even in EPlay.Local. Both must be ME here.
        int previous = ReadInt(result, 0x28);
        Marshal.WriteInt32(result, 0x28, 0); // TransferSide.EControlType.ME
        Log.LogInfo("BATTLE SIDE " + Marshal.ReadByte(side, 0x18) + ": control " + previous + " -> ME (local human).");
        return result;
    }

    bool OnOwnsSide(IntPtr self, int playerIndex, int sideIndex, IntPtr method)
    {
        // Extend the existing local player's side list, rather than bypass command
        // execution/phase checks. The normal stream still records and processes picks,
        // rerolls and battle commands. Never grant a new sender or an extra side id.
        if (!failed && logic != IntPtr.Zero && ownedPlayer == IntPtr.Zero && (sideIndex == 0 || sideIndex == 1))
        {
            try
            {
                IntPtr info = Required(ReadPtr(logic, 0x18));
                bool local = ReadInt(info, 0x18) == 0;
                if (local && (originalOwnsSide(self, playerIndex, 0, method) || originalOwnsSide(self, playerIndex, 1, method)))
                {
                    IntPtr players = Required(ReadPtr(Required(ReadPtr(self, 0x38)), 0x10));
                    if (playerIndex < 0 || playerIndex >= ReadInt(players, 0x18)) return originalOwnsSide(self, playerIndex, sideIndex, method);
                    IntPtr player = Required(ReadPtr(players, 0x20 + playerIndex * IntPtr.Size));
                    IntPtr oldSides = Required(ReadPtr(player, 0x30));
                    if (ReadInt(oldSides, 0x18) == 1)
                    {
                        IntPtr newSides = Required(Native.il2cpp_array_new_specific(Native.il2cpp_object_get_class(oldSides), (UIntPtr)2));
                        IntPtr temporary = Native.il2cpp_gchandle_new(newSides, false);
                        try
                        {
                            Marshal.WriteInt32(newSides, 0x20, 0);
                            Marshal.WriteInt32(newSides, 0x24, 1);
                            ownedPlayerHandle = Native.il2cpp_gchandle_new(player, false);
                            originalSidesHandle = Native.il2cpp_gchandle_new(oldSides, false);
                            ownedPlayer = player; originalOwnedSides = oldSides; replacementOwnedSides = newSides;
                            WriteRef(player, 0x30, newSides);
                            Log.LogInfo("LOCAL OWNERSHIP: existing player " + playerIndex + " now controls sides [0,1].");
                        }
                        finally { Native.il2cpp_gchandle_free(temporary); }
                    }
                }
            }
            catch (Exception e) { Fail("Local side ownership", e); }
        }
        return originalOwnsSide(self, playerIndex, sideIndex, method);
    }

    void OnDestroy(IntPtr self, IntPtr method)
    {
        if (logic != IntPtr.Zero && ReadPtr(self, 0x50) == logic) ResetSession();
        originalDestroy(self, method);
    }

    void ResetSession()
    {
        if (ownedPlayer != IntPtr.Zero && ReadPtr(ownedPlayer, 0x30) == replacementOwnedSides)
            WriteRef(ownedPlayer, 0x30, originalOwnedSides);
        ownedPlayer = originalOwnedSides = replacementOwnedSides = IntPtr.Zero;
        if (ownedPlayerHandle != IntPtr.Zero) { Native.il2cpp_gchandle_free(ownedPlayerHandle); ownedPlayerHandle = IntPtr.Zero; }
        if (originalSidesHandle != IntPtr.Zero) { Native.il2cpp_gchandle_free(originalSidesHandle); originalSidesHandle = IntPtr.Zero; }
        pendingHandoff = handedOff = failed = false;
        controller = logic = readyScreen = displayedProfileHero = IntPtr.Zero;
        if (logicHandle != IntPtr.Zero) { Native.il2cpp_gchandle_free(logicHandle); logicHandle = IntPtr.Zero; }
        if (controllerHandle != IntPtr.Zero) { Native.il2cpp_gchandle_free(controllerHandle); controllerHandle = IntPtr.Zero; }
    }

    void Fail(string stage, Exception error)
    {
        failed = true; pendingHandoff = false;
        Log.LogError("HOTSEAT FAILED at " + stage + ": " + error + ". Exit this arena and keep the log for diagnosis.");
    }

    int GetPhase(IntPtr phase)
    {
        IntPtr method = Native.il2cpp_class_get_method_from_name(Native.il2cpp_object_get_class(phase), "bltc", 0);
        IntPtr boxed = Native.il2cpp_runtime_invoke(Required(method), phase, IntPtr.Zero, out IntPtr error);
        if (error != IntPtr.Zero) throw new InvalidOperationException("Game phase query raised an IL2CPP exception.");
        return Marshal.ReadInt32(Native.il2cpp_object_unbox(Required(boxed)));
    }

    IntPtr Address(int rva) => IntPtr.Add(module, rva);
    T Function<T>(int rva) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(Address(rva));
    void Check(int rva, string hex)
    {
        for (int i = 0; i < hex.Length / 2; i++)
            if (Marshal.ReadByte(Address(rva), i) != Convert.ToByte(hex.Substring(i * 2, 2), 16))
                throw new InvalidOperationException("Another patch or incompatible method at RVA " + rva.ToString("X"));
    }
    static IntPtr ReadPtr(IntPtr owner, int offset) => Marshal.ReadIntPtr(Required(owner), offset);
    static int ReadInt(IntPtr owner, int offset) => Marshal.ReadInt32(Required(owner), offset);
    static IntPtr Required(IntPtr value) => value != IntPtr.Zero ? value : throw new InvalidOperationException("Unexpected null IL2CPP object.");
    static void WriteRef(IntPtr owner, int offset, IntPtr value)
    {
        IntPtr field = IntPtr.Add(Required(owner), offset);
        Native.il2cpp_gc_wbarrier_set_field(owner, field, value);
    }

    static class Native
    {
        const string Game = "GameAssembly.dll";
        [DllImport("kernel32", CharSet=CharSet.Unicode)] internal static extern IntPtr GetModuleHandle(string name);
        [DllImport(Game, CallingConvention=CallingConvention.Cdecl)] internal static extern IntPtr il2cpp_object_get_class(IntPtr obj);
        [DllImport(Game, CallingConvention=CallingConvention.Cdecl)] internal static extern IntPtr il2cpp_class_get_name(IntPtr klass);
        [DllImport(Game, CallingConvention=CallingConvention.Cdecl)] internal static extern IntPtr il2cpp_array_new_specific(IntPtr arrayClass, UIntPtr size);
        [DllImport(Game, CallingConvention=CallingConvention.Cdecl)] internal static extern void il2cpp_gc_wbarrier_set_field(IntPtr obj, IntPtr field, IntPtr value);
        [DllImport(Game, CallingConvention=CallingConvention.Cdecl)] internal static extern IntPtr il2cpp_gchandle_new(IntPtr obj, [MarshalAs(UnmanagedType.I1)] bool pinned);
        [DllImport(Game, CallingConvention=CallingConvention.Cdecl)] internal static extern void il2cpp_gchandle_free(IntPtr handle);
        [DllImport(Game, CallingConvention=CallingConvention.Cdecl)] internal static extern IntPtr il2cpp_string_new([MarshalAs(UnmanagedType.LPUTF8Str)] string text);
        [DllImport(Game, CallingConvention=CallingConvention.Cdecl)] internal static extern IntPtr il2cpp_class_get_method_from_name(IntPtr klass, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int argumentCount);
        [DllImport(Game, CallingConvention=CallingConvention.Cdecl)] internal static extern IntPtr il2cpp_runtime_invoke(IntPtr method, IntPtr obj, IntPtr args, out IntPtr exception);
        [DllImport(Game, CallingConvention=CallingConvention.Cdecl)] internal static extern IntPtr il2cpp_object_unbox(IntPtr obj);
    }
}
