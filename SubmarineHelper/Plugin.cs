using Dalamud.Game.Command;
using ClickLib.Clicks;
using Dalamud.Plugin;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using Dalamud.Memory;
using System.Numerics;
using SubmarineHelper.Managers;
using Dalamud.Interface.Colors;
using FFXIVClientStructs.FFXIV.Client.Game;
using ImGuiNET;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System;
using Dalamud.Game.ClientState.Conditions;
using ClickLib;
using System.Collections.Generic;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using ECommons.Automation;
using ECommons;
using ECommons.Throttlers;

namespace SubmarineHelper
{
    public unsafe sealed class Plugin : IDalamudPlugin
    {
        public string Name => "SubmarineHelper";
        private const string CommandName = "/SubmarineHelper";

        private IDalamudPluginInterface PluginInterface { get; init; }
        private ICommandManager CommandManager { get; init; }
        public Configuration Configuration { get; init; }
        public WindowSystem WindowSystem = new("SubmarineHelper");

        private static AtkUnitBase* SelectString => (AtkUnitBase*)Service.Gui.GetAddonByName("SelectString");
        private static AtkUnitBase* SelectYesno => (AtkUnitBase*)Service.Gui.GetAddonByName("SelectYesno");
        // 航行结果
        private static AtkUnitBase* AirShipExplorationResult => (AtkUnitBase*)Service.Gui.GetAddonByName("AirShipExplorationResult");
        // 出发详情
        private static AtkUnitBase* AirShipExplorationDetail => (AtkUnitBase*)Service.Gui.GetAddonByName("AirShipExplorationDetail");
        //部件耐久
        private static AtkUnitBase* CompanyCraftSupply => (AtkUnitBase*)Service.Gui.GetAddonByName("CompanyCraftSupply");

        private static bool isRunning = false;

        private static bool IsOpen = false;
        private static bool hasStart = false;
        private static bool hasCollected = false;
        private static bool needConfirm = false;
        private static bool hasRepaired = false;
        private static int? RequisiteMaterials;

        private DateTime NextClick;

        private static readonly HashSet<uint> CompanyWorkshopZones = [423, 424, 425, 653, 984];

        private static Lazy<string> RequisiteMaterialsName => new(() => Service.DataManager.GetExcelSheet<Lumina.Excel.GeneratedSheets.Item>().GetRow(10373).Name.RawString);


        public Plugin(IDalamudPluginInterface pluginInterface, ICommandManager commandManager)
        {
            this.PluginInterface = pluginInterface;
            this.CommandManager = commandManager;
            this.Configuration = this.PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
            this.Configuration.Initialize(this.PluginInterface);
            Service.Initialize(pluginInterface);
            Click.Initialize();

            ECommonsMain.Init(pluginInterface, this);
            AutoCutsceneSkipper.Init(CutsceneSkipHandler);

            this.CommandManager.AddHandler("→_", new CommandInfo(OnCommand)
            {
                HelpMessage = ""
            });

            this.PluginInterface.UiBuilder.Draw += DrawUI;

            PluginInterface.UiBuilder.Draw += OverlayUI;
            Service.Framework.Update += this.OnTick;
        }

        public void Dispose()
        {
            ECommonsMain.Dispose();

            this.WindowSystem.RemoveAllWindows();
            this.CommandManager.RemoveHandler("→_");

            PluginInterface.UiBuilder.Draw -= OverlayUI;
            Service.Framework.Update -= this.OnTick;
        }

        private void OnCommand(string command, string args) { }

        private void OnTick(IFramework framework) {

            //不在工坊
            if (!CompanyWorkshopZones.Contains(Service.ClientState.TerritoryType)) {
                isRunning = false;
                return;
            }


            DateTime now = DateTime.Now;
            if (NextClick > now)
            {
                return;
            }
            if (isRunning && SelectYesno != null && IsAddonAndNodesReady(SelectYesno))
            {
                if (needConfirm) {
                    Click.SendClick("select_yes", (nint)SelectYesno);
                    needConfirm = false;
                }
            }
            if (isRunning && CompanyCraftSupply != null && IsAddonAndNodesReady(CompanyCraftSupply)) {
                if (!hasRepaired)
                {
                    RepairSubmarines();
                }
                else {
                    Click.SendClick("request_cancel", (nint)CompanyCraftSupply);
                }
            }
            if (isRunning && AirShipExplorationResult != null && IsAddonAndNodesReady(AirShipExplorationResult)) {
                if (!hasCollected)
                {
                    //确认结果
                    ClickJournalResult.Using((nint)AirShipExplorationResult).Complete();
                    hasCollected = true;
                }
                else {
                    //点击再次出发
                    Click.SendClick("request_hand_over", (nint)AirShipExplorationResult);
                }
            }
            if (isRunning && AirShipExplorationDetail != null && IsAddonAndNodesReady(AirShipExplorationDetail))
            {
                //确认出发
                Click.SendClick("request_hand_over", (nint)AirShipExplorationDetail);

                hasCollected = false;
                hasRepaired = false;

                hasStart = true;
            }
            if (SelectString != null && IsAddonAndNodesReady(SelectString)) {
                var title = MemoryHelper.ReadStringNullTerminated((nint)SelectString->AtkValues[2].String);
                if (!string.IsNullOrWhiteSpace(title) && title.Contains("请选择潜水艇"))
                {
                    IsOpen = true;
                }
                else 
                {
                    IsOpen = false;
                    if (isRunning) {
                        if (!hasRepaired)
                        {
                            if (!string.IsNullOrWhiteSpace(title) && title.Contains("级]"))
                            {
                                SelectStringText(SelectString, "修理");
                            }
                        }
                        else
                        {
                            if (!string.IsNullOrWhiteSpace(title) && title.Contains("级]"))
                            {
                                SelectStringText(SelectString, "上次的远航报告");
                            }
                        }
                    }
                }


                if (isRunning)
                {
                    var addon = Service.Gui.GetAddonByName("SelectString", 1);
                    if (addon != IntPtr.Zero)
                    {
                        var selectStrAddon = (AddonSelectString*)addon;
                        if (GenericHelpers.IsAddonReady(&selectStrAddon->AtkUnitBase))
                        {
                            //Service.Log.Warning($"1: {selectStrAddon->GetTextNodeById(2)->NodeText.ToString()}");
                            List<string> SkipCutsceneStr = ["Skip cutscene?", "要跳过这段过场动画吗？", "要跳過這段過場動畫嗎？", "Videosequenz überspringen?", "Passer la scène cinématique ?", "このカットシーンをスキップしますか？"];
                            if (SkipCutsceneStr.Contains(selectStrAddon->AtkUnitBase.UldManager.NodeList[3]->GetAsAtkTextNode()->NodeText.ToString()))
                            {
                                if (EzThrottler.Throttle("SkipCutsceneConfirm"))
                                {
                                    //Service.Log.Warning("Selecting cutscene skipping");
                                    Callback.Fire((AtkUnitBase*)addon, true, 0);
                                }
                            }
                        }
                    }
                }
            }
            if (hasStart)
            {
                GetSubmarineInfos();
            }

            NextClick = DateTime.Now.AddSeconds(0.5);
        }

        bool CutsceneSkipHandler(nint ptr)
        {
            if (!CompanyWorkshopZones.Contains(Service.ClientState.TerritoryType)) return false; //不在工坊
            return true;
        }

        private bool? GetSubmarineInfos()
        {
            // 还在看动画
            if (Service.Condition[ConditionFlag.OccupiedInCutSceneEvent] ||
                Service.Condition[ConditionFlag.WatchingCutscene78]) return false;

            var inventoryManager = InventoryManager.Instance();
            if (inventoryManager->GetInventoryItemCount(10373) < 30)
            {
                hasStart = false;
                Service.Chat.PrintError("[SubmarineHelper] 魔导机械修理材料不足 30 。");
                return true;
            }

            if (inventoryManager->GetInventoryItemCount(10155) < 99)
            {
                hasStart = false;
                Service.Chat.PrintError("[SubmarineHelper] 青磷水不足 99 。");
                return true;
            }

            if (SelectString == null || !IsAddonAndNodesReady(SelectString)) return false;
            if (SelectStringText(SelectString, "探索完成"))
            {
                hasStart = false;
                return true;
            }

            return false;
        }
        private static bool? RepairSubmarines()
        {
            if (CompanyCraftSupply == null || !IsAddonAndNodesReady(CompanyCraftSupply)) return false;
            if (needConfirm) return false;

            for (var i = 0; i < 4; i++)
            {
                var endurance = CompanyCraftSupply->AtkValues[3 + (8 * i)].UInt;
                if (endurance <= 0)
                {
                    AgentHelper.SendEvent(AgentId.SubmersibleParts, 0, 3, 0, i, 0, 0, 0);
                    needConfirm = true;
                    return false;
                }
            }

            hasRepaired = true;

            return true;
        }

        public void OverlayUI()
        {
            if (SelectString == null)
            {
                IsOpen = false;
                return;
            }
            if (!IsOpen) return;


            if (ImGui.Begin("", ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoTitleBar))
            {
                var pos = new Vector2(SelectString->GetX() + 6, SelectString->GetY() - 40);
                ImGui.SetWindowPos(pos);


                ImGui.AlignTextToFramePadding();

                ImGui.SameLine();

                ImGui.BeginDisabled(isRunning);
                if (ImGui.Button("开始")) {
                    hasStart = true;
                    isRunning = true;
                }
                ImGui.EndDisabled();

                ImGui.SameLine();
                if (ImGui.Button("停止")) {
                    isRunning = false;
                }

                RequisiteMaterials ??= InventoryManager.Instance()->GetInventoryItemCount(10373);

                ImGui.SameLine();
                ImGui.Text($"{RequisiteMaterialsName.Value}:");

                ImGui.SameLine();
                ImGui.TextColored(RequisiteMaterials < 30 ? ImGuiColors.DPSRed : ImGuiColors.HealerGreen,
                                  RequisiteMaterials.ToString());

                ImGui.SameLine();
                ImGui.Text("     运行状态: ");

                ImGui.SameLine();
                ImGui.TextColored(isRunning ? ImGuiColors.HealerGreen : ImGuiColors.DPSRed,
                                  isRunning ? "已开始" : "未开始");

                ImGui.End();
            }
        }

        private bool SelectStringText(AtkUnitBase* addon, string text) {
            IntPtr addonByName = (IntPtr)addon;
            if (TryScanSelectStringText(addon, text, out var index)) {
                ClickSelectString.Using(addonByName).SelectItem((ushort)index);
                return true;
            }
            return false;
        }

        public static bool TryScanSelectStringText(AtkUnitBase* addon, string text, out int index)
        {
            index = -1;
            if (addon == null) return false;

            var entryCount = ((AddonSelectString*)addon)->PopupMenu.PopupMenu.EntryCount;
            for (var i = 0; i < entryCount; i++)
            {
                var currentString = MemoryHelper.ReadStringNullTerminated((nint)addon->AtkValues[i + 7].String);
                if (!currentString.Contains(text, StringComparison.OrdinalIgnoreCase)) continue;

                index = i;
                return true;
            }

            return false;
        }

        public static bool IsAddonAndNodesReady(AtkUnitBase* UI) => UI != null && UI->IsVisible && UI->UldManager.LoadedState == AtkLoadState.Loaded && UI->RootNode != null && UI->RootNode->ChildNode != null && UI->UldManager.NodeList != null;

        private void DrawUI()
        {
            this.WindowSystem.Draw();
        }

    }
}
