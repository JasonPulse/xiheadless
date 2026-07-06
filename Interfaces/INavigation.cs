namespace XiHeadless.Interfaces;

/// Navmesh-backed movement. MoveTo/Follow walk the bot along walkable polygons (no
/// teleporting). IsMoving is true while a path is in progress.
public interface INavigation
{
    bool IsMoving { get; }
    void MoveTo(float x, float z);             // target height = current height
    void MoveTo(float x, float y, float z);    // explicit target height (cross-zone-line points)
    bool CanReach(float x, float y, float z);  // navmesh can path to within melee of the point (skip unreachable mobs)
    void Follow(uint entityId);
    void Face(uint entityId);                  // turn to face an entity (server rejects attacks if not facing)
    void Stop();
}
