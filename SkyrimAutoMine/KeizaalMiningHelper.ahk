; Keizaal mining helper - AutoHotkey v2
; F8  = toggle auto-mine (taps Activate on whatever vein you're looking at)
; F9  = stop immediately
; F10 = quit the script
; Only sends keys while Skyrim is the active window.

#Requires AutoHotkey v2.0
#SingleInstance Force

ActivateKey  := "e"      ; your Activate key
TapInterval  := 4000     ; ms between taps; raise it if it interrupts your swings
MaxTaps      := 15       ; auto-stops after this many taps so it never runs forever
GameWindow   := "ahk_exe SkyrimSE.exe"

running := false
taps := 0

F8:: {
    global running, taps
    if running {
        StopMining()
        return
    }
    if !WinActive(GameWindow) {
        ToolTip("Skyrim isn't the active window")
        SetTimer(() => ToolTip(), -1500)
        return
    }
    running := true
    taps := 0
    ToolTip("Auto-mine ON (F8/F9 to stop)")
    TapActivate()
    SetTimer(TapActivate, TapInterval)
}

F9::StopMining()
F10::ExitApp()

TapActivate() {
    global taps
    ; Stop if you alt-tab away or hit the cap
    if !WinActive(GameWindow) || taps >= MaxTaps {
        StopMining()
        return
    }
    Send("{" ActivateKey " down}")
    Sleep(60)
    Send("{" ActivateKey " up}")
    taps += 1
}

StopMining() {
    global running
    running := false
    SetTimer(TapActivate, 0)
    ToolTip("Auto-mine OFF")
    SetTimer(() => ToolTip(), -1500)
}
