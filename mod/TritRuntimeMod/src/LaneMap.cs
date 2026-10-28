#nullable enable

using System;

namespace TritRuntimeMod;

/// <summary>
/// 世界投影的车道键编码。
/// </summary>
/// <remarks>
/// <para>
/// 一个 <see cref="uint"/> 把"哪个实体的哪个字段"压成一个稳定车道号：
/// <c>[31..28] 种类 | [27..12] 槽位 | [11..0] 字段</c>。
/// </para>
/// <para>
/// 三进制内核只见到纯整数与三值增量，永远看不到 <c>NPC</c>/<c>Projectile</c> 对象——
/// 这是"Rust 不持有 Unity 对象"的硬约束在编码层的落地。
/// </para>
/// <para>
/// 编码一旦发布就是 ABI：<c>TritRuntime.Query</c> / 快照 / 差分的字节都依赖它，
/// 改动必须同步升 <see cref="LayoutVersion"/>。
/// </para>
/// </remarks>
public static class LaneMap
{
    /// <summary>车道编码版本。改变任一常量必须递增。</summary>
    public const uint LayoutVersion = 1;

    public const uint KindNpc = 1;
    public const uint KindProjectile = 2;
    public const uint KindPlayer = 3;

    // 字段号（12 位空间，按种类各自解释）
    public const uint FieldLife = 0x001;
    public const uint FieldLifeMax = 0x002;
    public const uint FieldActive = 0x003;
    public const uint FieldMana = 0x004;
    public const uint FieldPositionX = 0x005;
    public const uint FieldPositionY = 0x006;
    public const uint FieldVelocityX = 0x007;
    public const uint FieldVelocityY = 0x008;
    public const uint FieldDirection = 0x009;

    /// <summary>槽位上界（16 位）。</summary>
    public const int MaxSlot = 0xFFFF;

    /// <summary>字段上界（12 位）。</summary>
    public const uint MaxField = 0xFFF;

    /// <summary>编码一条车道键。</summary>
    public static uint Key(uint kind, int slot, uint field)
    {
        if (kind > 0xF)
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "种类必须是 4 位（0..15）");
        }

        if (slot < 0 || slot > MaxSlot)
        {
            throw new ArgumentOutOfRangeException(nameof(slot), slot, "槽位必须是 16 位无符号（0..65535）");
        }

        if (field > MaxField)
        {
            throw new ArgumentOutOfRangeException(nameof(field), field, "字段号必须是 12 位（0..4095）");
        }

        return (kind << 28) | ((uint)slot << 12) | field;
    }

    /// <summary>拆出种类。</summary>
    public static uint KindOf(uint key) => (key >> 28) & 0xFu;

    /// <summary>拆出槽位。</summary>
    public static int SlotOf(uint key) => (int)((key >> 12) & 0xFFFFu);

    /// <summary>拆出字段号。</summary>
    public static uint FieldOf(uint key) => key & MaxField;

    /// <summary>字段号 → 可读名（日志 / 诊断用）。</summary>
    public static string FieldName(uint field) => field switch
    {
        FieldLife => "life",
        FieldLifeMax => "lifeMax",
        FieldActive => "active",
        FieldMana => "mana",
        FieldPositionX => "posX",
        FieldPositionY => "posY",
        FieldVelocityX => "velX",
        FieldVelocityY => "velY",
        FieldDirection => "direction",
        _ => "field#" + field.ToString("X3"),
    };

    /// <summary>种类 → 可读名。</summary>
    public static string KindName(uint kind) => kind switch
    {
        KindNpc => "npc",
        KindProjectile => "projectile",
        KindPlayer => "player",
        _ => "kind#" + kind,
    };

    /// <summary>车道键 → 可读名，如 <c>npc[12].life</c>。</summary>
    public static string Describe(uint key) =>
        KindName(KindOf(key)) + "[" + SlotOf(key) + "]." + FieldName(FieldOf(key));
}
