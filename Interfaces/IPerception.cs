namespace XiHeadless.Interfaces;

/// Read-only view of the world for decision-making.
public interface IPerception
{
    WorldState World { get; }
    Entity? Nearest(Func<Entity, bool> match);
    float DistanceTo(float x, float z);
    // 3D distance (includes the Y/height gap). The SERVER's melee range check is 3D (CBattleEntity::CanAttack ->
    // distance() with ignoreVertical=false), so a mob on a slope/ledge at a different Y is OUT of melee even when
    // it's 2D-close — using 2D here made the WAR stand in fake melee dealing ZERO damage. Use this for melee.
    float DistanceTo3D(float x, float y, float z);
    // How many distinct mobs have attacked `id` (our MyId or a party member) within the recent window —
    // read from 0x028 action packets. Lets the brain SEE adds / a mob switching to the healer.
    int AttackersOn(uint id, long withinMs = 6000);
    // Names of those recent attackers (for logging "2 mobs attacking: Goblin, Sylvestre").
    IReadOnlyList<string> AttackerNames(uint id, long withinMs = 6000);
}
