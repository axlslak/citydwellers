using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization.Formatters.Binary;
using AOSharp.Common.GameData;
using org.critterai.nav;
using CritterVector3 = org.critterai.Vector3;

namespace CityBuddies
{
    internal sealed class NavmeshPathfinder
    {
        private const int MaximumPathPolygons = 2048;
        private const int MaximumStraightPathPoints = 512;

        private static readonly CritterVector3 NearestPointExtents =
            new CritterVector3(0.5f, 2.0f, 0.5f);

        // NavmeshQuery owns only a native pointer into this object. Keep the
        // managed Navmesh alive for the entire query lifetime; otherwise its
        // finalizer can release dtNavMesh while GetNearestPoint is still used.
        private readonly Navmesh _navmesh;
        private readonly NavmeshQuery _query;
        private readonly NavmeshQueryFilter _filter;

        private NavmeshPathfinder(Navmesh navmesh)
        {
            if (navmesh == null)
                throw new ArgumentNullException(nameof(navmesh));

            _navmesh = navmesh;
            _filter = new NavmeshQueryFilter();

            NavmeshQuery query;
            NavStatus status = NavmeshQuery.Create(
                _navmesh,
                MaximumPathPolygons,
                out query);
            if (NavUtil.Failed(status))
                throw new InvalidOperationException(
                    "CritterAI could not create the navmesh query: " + status);

            _query = query;
        }

        internal static NavmeshPathfinder Load(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                throw new FileNotFoundException(
                    "Navmesh file was not found.",
                    filePath);

            byte[] data;
            var formatter = new BinaryFormatter();
            using (var stream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read))
            {
                data = formatter.Deserialize(stream) as byte[];
            }
            if (data == null || data.Length == 0)
                throw new InvalidDataException(
                    "Navmesh file did not contain serialized navmesh bytes.");

            Navmesh navmesh;
            NavStatus status = Navmesh.Create(data, out navmesh);
            if (NavUtil.Failed(status) || navmesh == null)
                throw new InvalidDataException(
                    "CritterAI could not create the navmesh: " + status);

            return new NavmeshPathfinder(navmesh);
        }

        internal IReadOnlyList<Vector3> FindStraightPath(
            Vector3 start,
            Vector3 destination)
        {
            NavmeshPoint origin = GetNearestPoint(start, "start");
            NavmeshPoint end = GetNearestPoint(destination, "destination");

            var polygonPath = new uint[MaximumPathPolygons];
            int polygonCount;

            if (origin.polyRef == end.polyRef)
            {
                polygonPath[0] = origin.polyRef;
                polygonCount = 1;
            }
            else
            {
                NavStatus status = _query.FindPath(
                    origin,
                    end,
                    _filter,
                    polygonPath,
                    out polygonCount);
                if (NavUtil.Failed(status) || polygonCount == 0)
                    throw new InvalidOperationException(
                        "CritterAI could not find a navmesh path: " + status);

                if (polygonPath[polygonCount - 1] != end.polyRef)
                    throw new InvalidOperationException(
                        "CritterAI returned only a partial navmesh path.");
            }

            var straightPath =
                new CritterVector3[MaximumStraightPathPoints];
            int straightPathCount;
            NavStatus straightStatus = _query.GetStraightPath(
                origin.point,
                end.point,
                polygonPath,
                0,
                polygonCount,
                straightPath,
                null,
                null,
                out straightPathCount);
            if (NavUtil.Failed(straightStatus) || straightPathCount == 0)
                throw new InvalidOperationException(
                    "CritterAI could not straighten the navmesh path: " +
                    straightStatus);

            var result = new List<Vector3>(straightPathCount);
            for (int i = 0; i < straightPathCount; i++)
            {
                CritterVector3 point = straightPath[i];
                result.Add(new Vector3(point.x, point.y, point.z));
            }

            return result;
        }

        private NavmeshPoint GetNearestPoint(Vector3 position, string label)
        {
            NavmeshPoint point;
            NavStatus status = _query.GetNearestPoint(
                new CritterVector3(position.X, position.Y, position.Z),
                NearestPointExtents,
                _filter,
                out point);
            if (NavUtil.Failed(status) || point.polyRef == 0)
                throw new InvalidOperationException(
                    $"The {label} position is not on the navmesh.");

            return point;
        }
    }
}
