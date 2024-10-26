namespace Offsets
{
    public struct UnityList
    {
        public const uint Base = 0x10; // to UnityListBase
        public const uint Count = 0x18; // int32
    }

    public struct UnityDictionary
    {
        public const uint Base = 0x18; // to Base
        public const uint Count = 0x40; // int32
    }

    public struct UnityListBase
    {
        public const uint Start = 0x20; // start of list +(i * 0x8)
    }

    public struct UnityString
    {
        public const uint Length = 0x10; // int32
        public const uint Value = 0x14; // string,unicode
    }

    public struct ModuleBase
    {
        public const ulong GameObjectManager = 0x1D204B0; // to GameObjectManager
    }

    public struct UnityClass
    {
        public static readonly uint[] Name = new uint[] { 0x0, 0x0, 0x48 }; // to ClassName
    }

    public struct UnityComponent
    {
        public static readonly uint[] To_GameObject = new uint[] { 0x10, 0x30 };
        public static readonly uint[] To_Component = new uint[] { GameObject.ObjectClass, 0x18, 0x28 };
    }

    public struct GameObject
    {
        public const uint ObjectClass = 0x30;
        public const uint ObjectName = 0x60; // string,default (null terminated)
    }

    public struct MirrorSyncList
    {
        public const ulong ToList = 0x60;
    }

    public struct Hashset
    {
        public const uint Size = 0x10;
        public const uint Base = 0x18;
        public const uint Start = 0x28;
        public const uint Count = 0x3C;
    }

    public struct Manager {
        public const uint Players = 0x70;
        public const uint DiceGamePlayManager = 0x80;
        public const uint RouletteGamePlayManager = 0x88;
        public const uint BlorfGamePlayManager = 0x90;
        public const uint GameStarted = 0x204;
        public const uint GameMode = 0x208;
    }

    public struct PlayerStats {
        public const uint PlayerName = 0x68;
        public const uint Dead = 0x88;
    }

    public struct DiceGamePlay {
        public const uint DiceValues = 0xF0; // Mirror.SyncList
    }

    public struct DiceGamePlayManager {
        public const uint DiceMode = 0x170;
    }

    public struct BlorfGamePlay {
        public const uint CardTypes = 0x138; // -1 = Devil, 1 = King, 2 = Queen, 3 = Ace, 4 = Joker
        public static readonly char[] CardMarking = ['D', '-', 'K', 'Q', 'A', 'J'];
        public const uint CurrentRevolver = 0x194;
        public const uint RevolverBullet = 0x198;
    }

    public struct BlorfGamePlayManager {
        public const uint LastRound = 0x70; // Mirror.SyncList
    }
}