// Shared data types for the BotController API.

using System.Runtime.InteropServices;

namespace BotControllerApi
{
    // Lock category.
    //   All    - freezes both CCSBot::Update and CCSBot::Upkeep
    //   Aim    - freezes CCSBot::Upkeep only
    //   Weapon - locks the bot's weapon to a specific engine slot
    //   Jump   - blocks CCSBot::Jump only
    public enum LockKind
    {
        All = 0,
        Aim = 1,
        Weapon = 2,
        Jump = 3,
    }

    // Engine weapon slots.
    public enum LockTarget
    {
        None = 0,
        Slot1 = 1,
        Slot2 = 2,
        Slot3 = 3,
        Slot4 = 4,
        Slot5 = 5,
    }

    /** One boundary of a movement tick. Captured pre (before mover) and post (after) */
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct MovementSnapshot
    {
        public float OriginX, OriginY, OriginZ;
        public float VelX, VelY, VelZ;
        public float Pitch, Yaw, Roll;
        public uint EntityFlags;
        public byte MoveType;
        public byte Pad0, Pad1, Pad2;
        public ulong Buttons;        // states[0] (pressed)
        public ulong Buttons1;       // states[1]
        public ulong Buttons2;       // states[2]
        public float DuckAmount;     // m_flDuckAmount (0=stand, 1=full crouch)
        public float DuckSpeed;      // m_flDuckSpeed
        public float LadderNormalX;  // m_vecLadderNormal
        public float LadderNormalY;
        public float LadderNormalZ;
        public byte Ducked;         // m_bDucked
        public byte Ducking;        // m_bDucking
        public byte DesiresDuck;    // m_bDesiresDuck
        public byte ActualMoveType; // m_nActualMoveType
    }

    /** One recorded server tick. Must match C++ ReplayTick byte layout exactly */
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct ReplayTick
    {
        public MovementSnapshot Pre;
        public MovementSnapshot Post;
        public int WeaponDefIndex;
        public uint NumSubtick;
    }

    /** One subtick input step. Must match C++ SubtickMove byte layout exactly */
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct SubtickMove
    {
        public float When;
        public uint Button;
        public float Pressed;
        public float AnalogForward;
        public float AnalogLeft;
        public float PitchDelta;
        public float YawDelta;
    }

    /** Optional replay usercmd frame. Must match C++ ReplayCommandFrameData */
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct ReplayCommandFrame
    {
        public float ForwardMove;
        public float LeftMove;
        public float UpMove;
        public float Pitch;
        public float Yaw;
        public float Roll;
        public ulong Buttons;
        public ulong Buttons1;
        public ulong Buttons2;
        public int MouseDx;
        public int MouseDy;
        public int WeaponSelect;
        public uint Fields;
        public byte LeftHandDesired;
        public byte Pad0;
        public byte Pad1;
        public byte Pad2;
    }

    /** Optional offset-backed replay movement state. Must match C++ ReplayMovementExtra */
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct ReplayMovementExtra
    {
        public uint Fields;
        public float JumpPressedTime;
        public float LastDuckTime;
        public int LastActualJumpPressTick;
        public float LastActualJumpPressFrac;
        public int LastUsableJumpPressTick;
        public float LastUsableJumpPressFrac;
        public int LastLandedTick;
        public float LastLandedFrac;
        public float LastLandedVelocityX;
        public float LastLandedVelocityY;
        public float LastLandedVelocityZ;
    }

    /** Bot personality / aim / weapon preference. Mirrors C++ BotProfileData */
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct BotProfileData
    {
        public float Aggression;    // 0..1
        public float Skill;         // 0..1
        public float Teamwork;      // 0..1
        public float ReactionTime;  // seconds
        public float AttackDelay;   // seconds
        public float LookAccelAtk;  // m_lookAngleMaxAccelAttacking
        public float LookStiffAtk;  // m_lookAngleStiffnessAttacking
        public float LookDampAtk;   // m_lookAngleDampingAttacking
        public int Cost;
        public int Difficulty;      // bitmask EASY/NORMAL/HARD/EXPERT
        public int WeaponPrefCount;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public ushort[] WeaponPref; // item def index, [0..WeaponPrefCount)
    }
}
