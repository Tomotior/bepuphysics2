﻿using BepuPhysics.Collidables;
using BepuUtilities;
using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using static BepuUtilities.GatherScatter;

namespace BepuPhysics.CollisionDetection.CollisionTasks
{
    public struct TrianglePairTester : IPairTester<TriangleWide, TriangleWide, Convex4ContactManifoldWide>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void GetIntervalForNormal(in Vector3Wide a, in Vector3Wide b, in Vector3Wide c, in Vector3Wide normal, out Vector<float> min, out Vector<float> max)
        {
            Vector3Wide.Dot(normal, a, out var dA);
            Vector3Wide.Dot(normal, b, out var dB);
            Vector3Wide.Dot(normal, c, out var dC);
            min = Vector.Min(dA, Vector.Min(dB, dC));
            max = Vector.Max(dA, Vector.Max(dB, dC));
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void GetDepthForNormal(in Vector3Wide aA, in Vector3Wide bA, in Vector3Wide cA, in Vector3Wide aB, in Vector3Wide bB, in Vector3Wide cB,
            in Vector3Wide normal, out Vector<float> depth)
        {
            GetIntervalForNormal(aA, bA, cA, normal, out var minA, out var maxA);
            GetIntervalForNormal(aB, bB, cB, normal, out var minB, out var maxB);
            depth = Vector.Min(maxA - minB, maxB + minA);
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void TestEdgeEdge(
            in Vector3Wide edgeDirectionA, in Vector3Wide edgeDirectionB,
            in Vector3Wide aA, in Vector3Wide bA, in Vector3Wide cA, in Vector3Wide aB, in Vector3Wide bB, in Vector3Wide cB,
            out Vector<float> depth, out Vector3Wide normal)
        {
            //Calibrate the normal to point from the triangle to the box while normalizing.
            Vector3Wide.CrossWithoutOverlap(edgeDirectionA, edgeDirectionB, out normal);
            Vector3Wide.Length(normal, out var normalLength);
            //Note that we do not calibrate yet. The depth calculation does not rely on calibration, so we punt it until after all normals have been tested.
            Vector3Wide.Scale(normal, Vector<float>.One / normalLength, out normal);
            GetDepthForNormal(aA, bA, cA, aB, bB, cB, normal, out depth);
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void Select(
            ref Vector<float> depth, ref Vector3Wide normal,
            in Vector<float> depthCandidate, in Vector3Wide normalCandidate)
        {
            var useCandidate = Vector.LessThan(depthCandidate, depth);
            Vector3Wide.ConditionalSelect(useCandidate, normalCandidate, normal, out normal);
            depth = Vector.Min(depth, depthCandidate);
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void Select(
            ref Vector<float> depth, ref Vector3Wide normal,
            in Vector<float> depthCandidate, in Vector<float> nxCandidate, in Vector<float> nyCandidate, in Vector<float> nzCandidate)
        {
            var useCandidate = Vector.LessThan(depthCandidate, depth);
            normal.X = Vector.ConditionalSelect(useCandidate, nxCandidate, normal.X);
            normal.Y = Vector.ConditionalSelect(useCandidate, nyCandidate, normal.Y);
            normal.Z = Vector.ConditionalSelect(useCandidate, nzCandidate, normal.Z);
            depth = Vector.Min(depth, depthCandidate);
        }


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void Add(in Vector3Wide pointOnTriangle, in Vector3Wide triangleCenter, in Vector3Wide triangleTangentX, in Vector3Wide triangleTangentY, in Vector<int> featureId,
            in Vector<int> exists, ref ManifoldCandidate candidates, ref Vector<int> candidateCount)
        {
            Vector3Wide.Subtract(pointOnTriangle, triangleCenter, out var offset);
            ManifoldCandidate candidate;
            Vector3Wide.Dot(offset, triangleTangentX, out candidate.X);
            Vector3Wide.Dot(offset, triangleTangentY, out candidate.Y);
            candidate.FeatureId = featureId;
            ManifoldCandidateHelper.AddCandidate(ref candidates, ref candidateCount, candidate, exists);
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ClipEdge(in Vector3Wide edgeStart, in Vector3Wide edgeDirection, in Vector3Wide pointOnPlane, in Vector3Wide planeNormal, out Vector<float> entry, out Vector<float> exit)
        {
            //intersection = dot(edgeNormal, bA - edgeAStart) / dot(edgeNormal, edgeDirectionB)
            Vector3Wide.Subtract(edgeStart, pointOnPlane, out var edgeToPlane);
            Vector3Wide.Dot(edgeToPlane, planeNormal, out var edgePlaneNormalDot);
            Vector3Wide.Dot(edgeDirection, planeNormal, out var velocity);
            var t = edgePlaneNormalDot / velocity;
            var isEntry = Vector.GreaterThanOrEqual(velocity, Vector<float>.Zero);
            entry = Vector.ConditionalSelect(isEntry, t, new Vector<float>(float.MinValue));
            exit = Vector.ConditionalSelect(isEntry, new Vector<float>(float.MaxValue), t);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ClipBEdgeAgainstABounds(
             in Vector3Wide edgePlaneNormalAB, in Vector3Wide edgePlaneNormalBC, in Vector3Wide edgePlaneNormalCA,
             in Vector3Wide aA, in Vector3Wide aB,
             in Vector3Wide edgeDirectionB, in Vector3Wide edgeStartB, in Vector<int> entryId, in Vector<int> exitIdOffset,
             in Vector3Wide triangleCenterB, in Vector3Wide tangentBX, in Vector3Wide tangentBY,
             in Vector<float> epsilon, ref ManifoldCandidate candidates, ref Vector<int> candidateCount)
        {
            //The base id is the id of the vertex in the corner along the negative boxEdgeDirection and boxEdgeCenterOffsetDirection.
            //The edgeDirectionId is the amount to add when you move along the boxEdgeDirection to the other vertex.
            //The edgeCenterOffsetId is the amount to add when you move along the boxEdgeCenterOffsetDirection to the other vertex.

            //We have three edge planes created by the edges of triangle A.
            //We want to test the triangle B edge against all three of the edges.
            ClipEdge(edgeStartB, edgeDirectionB, aA, edgePlaneNormalAB, out var entryAB, out var exitAB);
            ClipEdge(edgeStartB, edgeDirectionB, aB, edgePlaneNormalBC, out var entryBC, out var exitBC);
            ClipEdge(edgeStartB, edgeDirectionB, aA, edgePlaneNormalCA, out var entryCA, out var exitCA);
            var entry = Vector.Max(entryAB, Vector.Max(entryBC, entryCA));
            var exit = Vector.Min(exitAB, Vector.Min(exitBC, exitCA));

            //entryX = dot(entry * edgeDirection + edgeStart - triangleCenter, tangentBX)
            //entryY = dot(entry * edgeDirection + edgeStart - triangleCenter, tangentBY)
            //exitX = dot(exit * edgeDirection + edgeStart - triangleCenter, tangentBX)
            //exitY = dot(exit * edgeDirection + edgeStart - triangleCenter, tangentBY)
            Vector3Wide.Subtract(edgeStartB, triangleCenterB, out var offset);
            Vector3Wide.Dot(offset, tangentBX, out var offsetX);
            Vector3Wide.Dot(offset, tangentBY, out var offsetY);
            Vector3Wide.Dot(tangentBX, edgeDirectionB, out var edgeDirectionX);
            Vector3Wide.Dot(tangentBY, edgeDirectionB, out var edgeDirectionY);

            ManifoldCandidate candidate;
            var six = new Vector<int>(6);
            //Entry
            var exists = Vector.BitwiseAnd(
                Vector.LessThan(candidateCount, six),
                Vector.BitwiseAnd(
                    Vector.GreaterThanOrEqual(exit - entry, epsilon),
                    Vector.GreaterThanOrEqual(entry, Vector<float>.Zero)));
            Vector3Wide.Scale(edgeDirectionB, entry, out var intersection);
            Vector3Wide.Add(intersection, edgeStartB, out intersection);
            Vector3Wide.Subtract(intersection, triangleCenterB, out intersection);
            candidate.X = entry * edgeDirectionX + offsetX;
            candidate.Y = entry * edgeDirectionY + offsetY;
            candidate.FeatureId = entryId;
            ManifoldCandidateHelper.AddCandidate(ref candidates, ref candidateCount, candidate, exists);
            //Exit
            exists = Vector.BitwiseAnd(
                Vector.LessThan(candidateCount, six),
                Vector.BitwiseAnd(
                    Vector.GreaterThanOrEqual(exit, entry),
                    Vector.LessThanOrEqual(exit, Vector<float>.One)));
            Vector3Wide.Scale(edgeDirectionB, exit, out intersection);
            Vector3Wide.Add(intersection, edgeStartB, out intersection);
            Vector3Wide.Subtract(intersection, triangleCenterB, out intersection);
            candidate.X = exit * edgeDirectionX + offsetX;
            candidate.Y = exit * edgeDirectionY + offsetY;
            candidate.FeatureId = entryId + exitIdOffset;
            ManifoldCandidateHelper.AddCandidate(ref candidates, ref candidateCount, candidate, exists);
        }


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void TryAddTriangleVertex(in Vector3Wide vertex, in Vector<int> vertexId,
            in Vector3Wide tangentBX, in Vector3Wide tangentBY, in Vector3Wide faceNormalB, in Vector3Wide triangleCenterB,
            in Vector3Wide edgePlaneNormalAB, in Vector3Wide edgePlaneNormalBC, in Vector3Wide edgePlaneNormalCA, in Vector3Wide aA, in Vector3Wide bA,
            ref ManifoldCandidate candidates, ref Vector<int> candidateCount)
        {
            //Test edge edge plane sign for all three edges of B.
            Vector3Wide.Subtract(aA, vertex, out var vertexToAA);
            Vector3Wide.Subtract(bA, vertex, out var vertexToBA);
            Vector3Wide.Dot(vertexToAA, edgePlaneNormalAB, out var abDot);
            Vector3Wide.Dot(vertexToBA, edgePlaneNormalBC, out var bcDot);
            Vector3Wide.Dot(vertexToAA, edgePlaneNormalCA, out var caDot);
            var abContained = Vector.GreaterThan(abDot, Vector<float>.Zero);
            var bcContained = Vector.GreaterThan(bcDot, Vector<float>.Zero);
            var caContained = Vector.GreaterThan(caDot, Vector<float>.Zero);
            var contained = Vector.BitwiseAnd(abContained, Vector.BitwiseAnd(bcContained, caContained));
            Add(vertex, triangleCenterB, tangentBX, tangentBY, vertexId, contained, ref candidates, ref candidateCount);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe void Test(
            ref TriangleWide a, ref TriangleWide b, ref Vector<float> speculativeMargin,
            ref Vector3Wide offsetB, ref QuaternionWide orientationA, ref QuaternionWide orientationB,
            out Convex4ContactManifoldWide manifold)
        {
            Matrix3x3Wide.CreateFromQuaternion(orientationA, out var worldRA);
            Matrix3x3Wide.CreateFromQuaternion(orientationB, out var worldRB);
            Matrix3x3Wide.MultiplyByTransposeWithoutOverlap(worldRB, worldRA, out var rB);
            Matrix3x3Wide.TransformByTransposedWithoutOverlap(offsetB, worldRA, out var localOffsetB);
            Matrix3x3Wide.Transform(b.A, rB, out var bA);
            Vector3Wide.Add(bA, localOffsetB, out bA);
            Matrix3x3Wide.Transform(b.B, rB, out var bB);
            Vector3Wide.Add(bB, localOffsetB, out bB);
            Matrix3x3Wide.Transform(b.C, rB, out var bC);
            Vector3Wide.Add(bC, localOffsetB, out bC);

            Vector3Wide.Add(bA, bB, out var localTriangleCenterB);
            Vector3Wide.Add(localTriangleCenterB, bC, out localTriangleCenterB);
            Vector3Wide.Scale(localTriangleCenterB, new Vector<float>(1f / 3f), out localTriangleCenterB);

            Vector3Wide.Subtract(bB, bA, out var abB);
            Vector3Wide.Subtract(bC, bB, out var bcB);
            Vector3Wide.Subtract(bA, bC, out var caB);

            Vector3Wide.Add(a.A, a.B, out var localTriangleCenterA);
            Vector3Wide.Add(localTriangleCenterA, a.C, out localTriangleCenterA);
            Vector3Wide.Scale(localTriangleCenterA, new Vector<float>(1f / 3f), out localTriangleCenterA);

            Vector3Wide.Subtract(a.B, a.A, out var abA);
            Vector3Wide.Subtract(a.C, a.B, out var bcA);
            Vector3Wide.Subtract(a.A, a.C, out var caA);

            //A AB x *
            TestEdgeEdge(abA, abB, a.A, a.B, a.C, bA, bB, bC, out var depth, out var localNormal);
            TestEdgeEdge(abA, bcB, a.A, a.B, a.C, bA, bB, bC, out var depthCandidate, out var localNormalCandidate);
            Select(ref depth, ref localNormal, depthCandidate, localNormalCandidate);
            TestEdgeEdge(abA, caB, a.A, a.B, a.C, bA, bB, bC, out depthCandidate, out localNormalCandidate);
            Select(ref depth, ref localNormal, depthCandidate, localNormalCandidate);

            //A BC x *
            TestEdgeEdge(bcA, abB, a.A, a.B, a.C, bA, bB, bC, out depthCandidate, out localNormalCandidate);
            Select(ref depth, ref localNormal, depthCandidate, localNormalCandidate);
            TestEdgeEdge(bcA, bcB, a.A, a.B, a.C, bA, bB, bC, out depthCandidate, out localNormalCandidate);
            Select(ref depth, ref localNormal, depthCandidate, localNormalCandidate);
            TestEdgeEdge(bcA, caB, a.A, a.B, a.C, bA, bB, bC, out depthCandidate, out localNormalCandidate);
            Select(ref depth, ref localNormal, depthCandidate, localNormalCandidate);

            //A CA x *
            TestEdgeEdge(caA, abB, a.A, a.B, a.C, bA, bB, bC, out depthCandidate, out localNormalCandidate);
            Select(ref depth, ref localNormal, depthCandidate, localNormalCandidate);
            TestEdgeEdge(caA, bcB, a.A, a.B, a.C, bA, bB, bC, out depthCandidate, out localNormalCandidate);
            Select(ref depth, ref localNormal, depthCandidate, localNormalCandidate);
            TestEdgeEdge(caA, caB, a.A, a.B, a.C, bA, bB, bC, out depthCandidate, out localNormalCandidate);
            Select(ref depth, ref localNormal, depthCandidate, localNormalCandidate);

            //Face normals
            Vector3Wide.CrossWithoutOverlap(abA, caA, out var faceNormalA);
            Vector3Wide.Length(faceNormalA, out var faceNormalALength);
            Vector3Wide.Scale(faceNormalA, Vector<float>.One / faceNormalALength, out faceNormalA);
            GetDepthForNormal(a.A, a.B, a.C, bA, bB, bC, faceNormalA, out depthCandidate);
            Select(ref depth, ref localNormal, depthCandidate, faceNormalA);
            Vector3Wide.CrossWithoutOverlap(abA, caA, out var faceNormalB);
            Vector3Wide.Length(faceNormalB, out var faceNormalBLength);
            Vector3Wide.Scale(faceNormalB, Vector<float>.One / faceNormalBLength, out faceNormalB);
            GetDepthForNormal(a.A, a.B, a.C, bA, bB, bC, faceNormalB, out depthCandidate);
            Select(ref depth, ref localNormal, depthCandidate, faceNormalB);

            //Point the normal from B to A by convention.
            Vector3Wide.Subtract(localTriangleCenterB, localTriangleCenterA, out var centerAToCenterB);
            Vector3Wide.Dot(localNormal, centerAToCenterB, out var calibrationDot);
            var shouldFlip = Vector.GreaterThan(calibrationDot, Vector<float>.Zero);
            localNormal.X = Vector.ConditionalSelect(shouldFlip, -localNormal.X, localNormal.X);
            localNormal.Y = Vector.ConditionalSelect(shouldFlip, -localNormal.Y, localNormal.Y);
            localNormal.Z = Vector.ConditionalSelect(shouldFlip, -localNormal.Z, localNormal.Z);

            //At this point, we have computed the minimum depth and associated local normal.
            //We now need to compute some contact locations, their per-contact depths, and the feature ids.

            //Contact generation always assumes face-face clipping. Other forms of contact generation are just special cases of face-face, and since we pay
            //for all code paths, there's no point in handling them separately.            

            //We will be working on the surface of the triangle, but we'd still like a 2d parameterization of the surface for contact reduction.
            //So, we'll create tangent axes from the edge and edge x normal.
            Vector3Wide.LengthSquared(abB, out var abBLengthSquared);
            Vector3Wide.Scale(abB, Vector<float>.One / Vector.SquareRoot(abBLengthSquared), out var tangentBX);
            Vector3Wide.CrossWithoutOverlap(tangentBX, faceNormalB, out var tangentBY);

            //Note that we only allocate up to 6 candidates. Each triangle edge can contribute at most two contacts (any more would require a nonconvex clip region).
            //Numerical issues can cause more to occur, but they're guarded against (both directly, and in the sense of checking count before adding any candidates beyond the sixth).
            int byteCount = Unsafe.SizeOf<ManifoldCandidate>() * 6;
            var buffer = stackalloc byte[byteCount];
            var candidateCount = Vector<int>.Zero;
            ref var candidates = ref Unsafe.As<byte, ManifoldCandidate>(ref *buffer);

            //While the edge clipping will find any edge-edge or aVertex-bFace contacts, it will not find bVertex-aFace contacts.
            //Add them independently.
            //(Adding these first allows us to simply skip capacity tests, since there can only be a total of three bVertex-aFace contacts.)
            Vector3Wide.Cross(abA, faceNormalA, out var edgePlaneNormalAB);
            Vector3Wide.Cross(bcA, faceNormalA, out var edgePlaneNormalBC);
            Vector3Wide.Cross(caA, faceNormalA, out var edgePlaneNormalCA);
            TryAddTriangleVertex(bA, Vector<int>.Zero, tangentBX, tangentBY, faceNormalB, localTriangleCenterB, edgePlaneNormalAB, edgePlaneNormalBC, edgePlaneNormalCA, a.A, a.B, ref candidates, ref candidateCount);
            TryAddTriangleVertex(bB, Vector<int>.One, tangentBX, tangentBY, faceNormalB, localTriangleCenterB, edgePlaneNormalAB, edgePlaneNormalBC, edgePlaneNormalCA, a.A, a.B, ref candidates, ref candidateCount);
            TryAddTriangleVertex(bC, new Vector<int>(2), tangentBX, tangentBY, faceNormalB, localTriangleCenterB, edgePlaneNormalAB, edgePlaneNormalBC, edgePlaneNormalCA, a.A, a.B, ref candidates, ref candidateCount);

            //Note that edge cases will also add triangle A vertices that are within triangle B's bounds, so no A vertex case is required.
            //Note that each of these calls can generate 4 contacts, so we have to start checking capacities.

            //Create a scale-sensitive epsilon for comparisons based on the size of the involved shapes. This helps avoid varying behavior based on how large involved objects are.
            Vector3Wide.LengthSquared(abA, out var abALengthSquared);
            Vector3Wide.LengthSquared(caA, out var caALengthSquared);
            Vector3Wide.LengthSquared(caB, out var caBLengthSquared);
            var epsilonScale = Vector.SquareRoot(Vector.Min(
                Vector.Max(abALengthSquared, caALengthSquared),
                Vector.Max(abBLengthSquared, caBLengthSquared)));
            var edgeEpsilon = new Vector<float>(1e-5f) * epsilonScale;
            var exitIdOffset = new Vector<int>(3);
            ClipBEdgeAgainstABounds(edgePlaneNormalAB, edgePlaneNormalBC, edgePlaneNormalCA, a.A, a.B, abB, bA, new Vector<int>(3), exitIdOffset, localTriangleCenterB, tangentBX, tangentBY, edgeEpsilon, ref candidates, ref candidateCount);
            ClipBEdgeAgainstABounds(edgePlaneNormalAB, edgePlaneNormalBC, edgePlaneNormalCA, a.A, a.B, bcB, bB, new Vector<int>(4), exitIdOffset, localTriangleCenterB, tangentBX, tangentBY, edgeEpsilon, ref candidates, ref candidateCount);
            ClipBEdgeAgainstABounds(edgePlaneNormalAB, edgePlaneNormalBC, edgePlaneNormalCA, a.A, a.B, caB, bC, new Vector<int>(5), exitIdOffset, localTriangleCenterB, tangentBX, tangentBY, edgeEpsilon, ref candidates, ref candidateCount);
            
            Vector3Wide.Subtract(localTriangleCenterA, localTriangleCenterB, out var faceCenterBToFaceCenterA);
            ManifoldCandidateHelper.Reduce(ref candidates, candidateCount, 6, faceNormalA, localNormal, faceCenterBToFaceCenterA, tangentBX, tangentBY, epsilonScale,
                out var contact0, out var contact1, out var contact2, out var contact3,
                out manifold.Contact0Exists, out manifold.Contact1Exists, out manifold.Contact2Exists, out manifold.Contact3Exists);

            //Transform the contacts into the manifold.
            var minimumAcceptedDepth = -speculativeMargin;
            //Move the basis into world rotation so that we don't have to transform the individual contacts.
            Matrix3x3Wide.TransformWithoutOverlap(tangentBX, worldRA, out var worldTangentBX);
            Matrix3x3Wide.TransformWithoutOverlap(tangentBY, worldRA, out var worldTangentBY);
            Matrix3x3Wide.TransformWithoutOverlap(localTriangleCenterB, worldRA, out var worldTriangleCenter);
            Matrix3x3Wide.TransformWithoutOverlap(localNormal, worldRA, out manifold.Normal);
            //If the local normal points against either triangle normal, then it's on the backside and should not collide.
            Vector3Wide.Dot(localNormal, faceNormalA, out var normalDotA);
            Vector3Wide.Dot(localNormal, faceNormalB, out var normalDotB);
            var allowContacts = Vector.BitwiseAnd(Vector.GreaterThanOrEqual(normalDotA, Vector<float>.Zero), Vector.GreaterThanOrEqual(normalDotB, Vector<float>.Zero));
            manifold.Contact0Exists = Vector.BitwiseAnd(manifold.Contact0Exists, allowContacts);
            manifold.Contact1Exists = Vector.BitwiseAnd(manifold.Contact1Exists, allowContacts);
            manifold.Contact2Exists = Vector.BitwiseAnd(manifold.Contact2Exists, allowContacts);
            manifold.Contact3Exists = Vector.BitwiseAnd(manifold.Contact3Exists, allowContacts);
            TransformContactToManifold(contact0, worldTriangleCenter, worldTangentBX, worldTangentBY, minimumAcceptedDepth, ref manifold.Contact0Exists, out manifold.OffsetA0, out manifold.Depth0, out manifold.FeatureId0);
            TransformContactToManifold(contact1, worldTriangleCenter, worldTangentBX, worldTangentBY, minimumAcceptedDepth, ref manifold.Contact1Exists, out manifold.OffsetA1, out manifold.Depth1, out manifold.FeatureId1);
            TransformContactToManifold(contact2, worldTriangleCenter, worldTangentBX, worldTangentBY, minimumAcceptedDepth, ref manifold.Contact2Exists, out manifold.OffsetA2, out manifold.Depth2, out manifold.FeatureId2);
            TransformContactToManifold(contact3, worldTriangleCenter, worldTangentBX, worldTangentBY, minimumAcceptedDepth, ref manifold.Contact3Exists, out manifold.OffsetA3, out manifold.Depth3, out manifold.FeatureId3);
        }


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void TransformContactToManifold(
            in ManifoldCandidate rawContact, in Vector3Wide faceCenterB, in Vector3Wide tangentBX, in Vector3Wide tangentBY, in Vector<float> minimumAcceptedDepth,
            ref Vector<int> contactExists, out Vector3Wide manifoldOffsetA, out Vector<float> manifoldDepth, out Vector<int> manifoldFeatureId)
        {
            Vector3Wide.Scale(tangentBX, rawContact.X, out manifoldOffsetA);
            Vector3Wide.Scale(tangentBY, rawContact.Y, out var y);
            Vector3Wide.Add(manifoldOffsetA, y, out manifoldOffsetA);
            Vector3Wide.Add(manifoldOffsetA, faceCenterB, out manifoldOffsetA);
            //Note that we delayed the speculative margin depth test until the end. This ensures area maximization has meaningful contacts to work with.
            //If we were more aggressive about the depth testing, the final manifold would tend to have more contacts, but less meaningful contacts.
            contactExists = Vector.BitwiseAnd(contactExists, Vector.GreaterThanOrEqual(rawContact.Depth, minimumAcceptedDepth));
            manifoldDepth = rawContact.Depth;
            manifoldFeatureId = rawContact.FeatureId;
        }

        public void Test(ref TriangleWide a, ref TriangleWide b, ref Vector<float> speculativeMargin, ref Vector3Wide offsetB, ref QuaternionWide orientationB, out Convex4ContactManifoldWide manifold)
        {
            throw new NotImplementedException();
        }

        public void Test(ref TriangleWide a, ref TriangleWide b, ref Vector<float> speculativeMargin, ref Vector3Wide offsetB, out Convex4ContactManifoldWide manifold)
        {
            throw new NotImplementedException();
        }
    }

    public class TrianglePairCollisionTask : CollisionTask
    {
        public TrianglePairCollisionTask()
        {
            BatchSize = 32;
            ShapeTypeIndexA = default(Triangle).TypeId;
            ShapeTypeIndexB = default(Triangle).TypeId;
        }


        //Every single collision task type will mirror this general layout.
        public unsafe override void ExecuteBatch<TCallbacks>(ref UntypedList batch, ref CollisionBatcher<TCallbacks> batcher)
        {
            ConvexCollisionTaskCommon.ExecuteBatch
                <TCallbacks,
                Triangle, TriangleWide, Triangle, TriangleWide, UnflippableTestPairWide<Triangle, TriangleWide, Triangle, TriangleWide>,
                Convex4ContactManifoldWide, TrianglePairTester>(ref batch, ref batcher);
        }
    }
}
