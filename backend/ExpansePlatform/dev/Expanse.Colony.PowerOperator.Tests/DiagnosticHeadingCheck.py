"""Offline direct rotation/source-frame check, explicitly not native geometry."""
from pathlib import Path
import math

workspace = Path(__file__).resolve().parents[3]
directory = workspace / "ExpansePlatform/dev/Expanse.ColonyPlacement.KspHarness"
terrain = (directory / "ColonyPlacementTerrainDiagnostic.cs").read_text()
support = (directory / "ColonyPlacementSupportTerrainDiagnostic.cs").read_text()
assert "double headingDegrees = 0" in terrain
assert "Quaternion.LookRotation(Quaternion.AngleAxis((float)headingDegrees, center.normal) * north, center.normal)" in terrain
assert "Quaternion.LookRotation(Quaternion.AngleAxis((float)request.HeadingDegrees, center.normal) * north, center.normal)" in support
assert "status.SurveyTerrainHeight, row, request.HeadingDegrees)" in support
assert 'node.AddValue("headingDegrees", R(headingDegrees))' in terrain
assert 'node.AddValue("surfaceFrame", frame.ToString("F9"))' in terrain
print("PASS Raw terrain and support projections use identical heading-about-native-normal frame; other callers retain heading0 default")

# For unit plane normal +Y and loaded north +Z, the exact positive rotation
# around that axis sends north to +X at 90 degrees. Rotated local X is -Z.
# Projecting a support point back through the inverse must recover its local
# coordinates, so the raw grid and sealed contacts refer to the same rectangle.
def world(x, z, heading):
    h = math.radians(heading)
    return (math.cos(h)*x + math.sin(h)*z, -math.sin(h)*x + math.cos(h)*z)

def local(east, north, heading):
    return world(east, north, -heading)

def near(a, b):
    assert all(abs(x-y) < 1e-12 for x, y in zip(a, b)), (a, b)

near(world(0, 1, 90), (1, 0))
near(world(1, 0, 90), (0, -1))
for x, z in ((-3,-7), (-3,9), (4,-7), (4,9), (0,0)):
    near(world(x,z,0), (x,z))
    near(local(*world(x,z,90),90), (x,z))
print("PASS Analytical90-degree rotation aligns raw rectangle/contact axes; heading0 remains unchanged")
print("2/2 offline source/direct-math checks passed. No native rays, contacts, terrain, clearance or qualification were measured.")
