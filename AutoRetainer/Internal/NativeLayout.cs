using AutoRetainer.Modules.GcHandin;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System;

namespace AutoRetainer.Internal;

/// <summary>
/// CS 沒有定義、AR 自己用的原生偏移。只在插件建構子裡從遊戲碼推導一次，之後唯讀。
/// 推不出來或指紋對不上就擲 InvalidOperationException 讓插件載入失敗：
/// 寧可不載入，也不帶著沒驗過的偏移去解參考或交給遊戲函式（那是攔不到的崩潰）。
/// 本檔只依賴 FFXIVClientStructs，離線探針直接編這個檔對執行檔驗（推導見 commit 訊息）。
/// </summary>
internal static unsafe class NativeLayout
{
    internal delegate bool TryScanText(string signature, out nint address);

    /// <summary>僱員物品指令（存入／取回／委託出售）的原生函式；Memory 也用這一條。</summary>
    internal const string RetainerItemCommandSignature = "48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 57 48 83 EC 30 48 8B 5C 24 ?? 41 8B F0";

    // 繳交清單被點選時讀那一筆的道具 ID：mov rdx,[r14+陣列]；movsxd rax,[r15+10h]；
    // imul rcx,rax,步距；mov [rbp+?],esi；mov ebx,[rcx+rdx+道具ID]
    internal const string SupplyListEntryReadSignature = "49 8B 96 ?? ?? ?? ?? 49 63 47 10 48 69 C8 ?? ?? ?? ?? 89 75 ?? 8B 9C 11 ?? ?? ?? ??";

    // 遊戲自己送僱員物品指令：mov edx,46h（AgentId.Retainer）；call 取代理人；test；je；
    // lea rcx,[rax+子物件]；mov qword [rsp+20h],指令；mov rax,[rcx]（之後 call [rax]）
    internal const string RetainerItemCommandCallSignature = "BA 46 00 00 00 E8 ?? ?? ?? ?? 48 85 C0 74 ?? 48 8D 48 ?? 48 C7 44 24 20 ?? 00 00 00 48 8B 01";

    /// <summary>AddonGrandCompanySupplyList 裡清單項目陣列指標的偏移；-1＝未推導，不可用。</summary>
    internal static int SupplyListEntriesOffset { get; private set; } = -1;

    /// <summary>僱員代理人裡物品指令子物件（原生函式的 this）的偏移；-1＝未推導或函式不存在，不可用。</summary>
    internal static int RetainerItemCommandOffset { get; private set; } = -1;

    internal static void Initialize(TryScanText scan)
    {
        SupplyListEntriesOffset = DeriveSupplyListEntriesOffset(scan);
        RetainerItemCommandOffset = DeriveRetainerItemCommandOffset(scan);
    }

    internal static int DeriveSupplyListEntriesOffset(TryScanText scan)
    {
        if(!scan(SupplyListEntryReadSignature, out var at) || at == 0)
            throw Fail("找不到稀有品繳交清單讀取項目的遊戲碼，無法確認項目陣列的位置。");
        var arrayOffset = *(int*)(at + 3);
        var stride = *(int*)(at + 14);
        var itemIdOffset = *(int*)(at + 24);
        GCExpectEntry layout = default;
        var expectedItemIdOffset = (int)((byte*)&layout.ItemID - (byte*)&layout);
        if(stride != sizeof(GCExpectEntry) || itemIdOffset != expectedItemIdOffset)
            throw Fail($"稀有品繳交清單的項目版面變了（步距 0x{stride:X}、道具 ID 在 0x{itemIdOffset:X}；插件預期 0x{sizeof(GCExpectEntry):X}、0x{expectedItemIdOffset:X}）。");
        if(arrayOffset < sizeof(AtkUnitBase) || arrayOffset > sizeof(AddonGrandCompanySupplyList) - sizeof(nint) || arrayOffset % sizeof(nint) != 0)
            throw Fail($"稀有品繳交清單的項目陣列偏移 0x{arrayOffset:X} 不在這扇視窗自己的欄位範圍內。");
        return arrayOffset;
    }

    internal static int DeriveRetainerItemCommandOffset(TryScanText scan)
    {
        // 函式本身對不上時物品指令整個停用（Memory 會記 Warning），偏移用不到，不擋載入。
        if(!scan(RetainerItemCommandSignature, out var function) || function == 0) return -1;
        if(!scan(RetainerItemCommandCallSignature, out var at) || at == 0)
            throw Fail("找不到遊戲送僱員物品指令的程式碼，無法確認要交給原生函式的位址。");
        var agentId = *(uint*)(at + 1);
        var offset = (int)*(sbyte*)(at + 18);
        if(agentId != (uint)AgentId.Retainer)
            throw Fail($"遊戲送僱員物品指令用的代理人編號 {agentId} 與插件編譯時的 {(uint)AgentId.Retainer} 不同。");
        if(offset < sizeof(AgentInterface) || offset % sizeof(nint) != 0)
            throw Fail($"僱員物品指令子物件的偏移 0x{offset:X} 不合理。");
        return offset;
    }

    /// <summary>呼叫端負責確認 <paramref name="addon"/> 是 GrandCompanySupplyList；回 null＝沒有清單或偏移不可用。</summary>
    internal static GCExpectEntry* GetSupplyListEntries(AtkUnitBase* addon)
        => SupplyListEntriesOffset < 0 ? null : *(GCExpectEntry**)((byte*)addon + SupplyListEntriesOffset);

    private static InvalidOperationException Fail(string what)
        => new($"[AutoRetainer] 遊戲版面驗證失敗，插件不載入（避免遊戲崩潰）：{what}");
}
