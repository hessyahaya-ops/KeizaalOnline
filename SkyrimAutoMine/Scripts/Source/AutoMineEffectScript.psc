Scriptname AutoMineEffectScript extends ActiveMagicEffect
{Lesser power: mines every ore vein near the player in one go.
 Needs SKSE (for Cell.GetNumRefs / GetNthRef). Works with vanilla MineOreScript veins,
 so depletion and respawn still follow the game's normal cell-reset rules.}

; --- Fill these in the Creation Kit ---
FormList Property mineOreToolsList Auto        ; vanilla list of pickaxes (same one MineOreScript uses)
Float Property SearchRadius = 1500.0 Auto      ; ~21 metres
Bool Property RequirePickaxe = True Auto
Bool Property PlayMiningSounds = True Auto
Sound Property MiningSound Auto                 ; optional, e.g. NPCHumanMiningPickaxeSD
Message Property NoPickaxeMessage Auto          ; optional, "You need a pickaxe."

Int Property kActivator = 24 AutoReadOnly       ; SKSE form type id for Activator

Event OnEffectStart(Actor akTarget, Actor akCaster)
	If akCaster != Game.GetPlayer()
		Return
	EndIf

	If RequirePickaxe && akCaster.GetItemCount(mineOreToolsList) < 1
		If NoPickaxeMessage
			NoPickaxeMessage.Show()
		Else
			Debug.Notification("You need a pickaxe to mine.")
		EndIf
		Return
	EndIf

	Cell here = akCaster.GetParentCell()
	If !here
		Return
	EndIf

	Int veinsMined = 0
	Int oreTotal = 0
	Int count = here.GetNumRefs(kActivator)
	Int i = 0
	While i < count
		ObjectReference ref = here.GetNthRef(i, kActivator)
		MineOreScript vein = ref as MineOreScript
		If vein && ref.IsEnabled() && akCaster.GetDistance(ref) <= SearchRadius
			Int got = MineVein(vein, akCaster)
			If got > 0
				veinsMined += 1
				oreTotal += got
			EndIf
		EndIf
		i += 1
	EndWhile

	If veinsMined > 0
		Debug.Notification("Mined " + veinsMined + " vein(s) for " + oreTotal + " ore.")
	Else
		Debug.Notification("No unmined ore veins nearby.")
	EndIf
EndEvent

; Strips a single vein and returns how much ore it gave.
Int Function MineVein(MineOreScript vein, Actor miner)
	If vein.GetState() == "depleted" || !vein.Ore
		Return 0
	EndIf

	; ResourceCountCurrent is -1 until the vein is first struck
	Int remaining = vein.ResourceCountCurrent
	If remaining < 0
		remaining = vein.ResourceCountTotal
	EndIf
	If remaining <= 0
		Return 0
	EndIf

	If PlayMiningSounds && MiningSound
		MiningSound.Play(vein)
		Utility.Wait(0.4)
	EndIf

	Int amount = remaining * vein.ResourceCount
	miner.AddItem(vein.Ore, amount)

	; 1 in 10 chance per vein of a gem, like vanilla mining
	If vein.lItemGems10 && Utility.RandomInt(1, 10) == 1
		miner.AddItem(vein.lItemGems10, 1)
	EndIf

	; Mark depleted the same way vanilla does so it respawns on cell reset
	vein.ResourceCountCurrent = 0
	vein.GoToState("depleted")
	Return amount
EndFunction
