using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.TestTools.Constraints;
using Is = UnityEngine.TestTools.Constraints.Is;
using System.Diagnostics;
using System.Collections;
using System.Runtime.CompilerServices;

public class AllocateTest {
	//System.Action handler = null;
	int hoge = 0;
	Stopwatch sw = new Stopwatch();

	//[Test]
	//public void QuickAllocateTest() {

	//	Assert.That(() => {
	//		sw.Reset();
	//		sw.Start();
	//		sw.Stop();
	//	}, Is.Not.AllocatingGCMemory());
	//	UnityEngine.Debug.LogWarning(sw.ElapsedTicks + hoge.ToString());
	//}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static void TR_Inverse(in Vector3 p, in Quaternion q, out Matrix4x4 m) {
		// NOTE:
		// QuaternionÇÕê≥ãKâªëOíÒ
		// InverseÇ»ÇÃÇ≈pÇ∆qÇÕîΩì]

		var x = -q.x * 2f;
		var y = -q.y * 2f;
		var z = -q.z * 2f;
		var xx = -q.x * x;
		var yy = -q.y * y;
		var zz = -q.z * z;
		var xy = -q.x * y;
		var xz = -q.x * z;
		var yz = -q.y * z;
		var wx = q.w * x;
		var wy = q.w * y;
		var wz = q.w * z;

		m.m00 = 1f - (yy + zz);
		m.m10 = xy + wz;
		m.m20 = xz - wy;
		m.m30 = 0f;

		m.m01 = xy - wz;
		m.m11 = 1f - (xx + zz);
		m.m21 = yz + wx;
		m.m31 = 0f;

		m.m02 = xz + wy;
		m.m12 = yz - wx;
		m.m22 = 1f - (xx + yy);
		m.m32 = 0f;

		//m.m03 = p.x;
		//m.m13 = p.y;
		//m.m23 = p.z;
		//m.m33 = 1f;
		m.m03 = m.m00 * -p.x + m.m01 * -p.y + m.m02 * -p.z; // + m.m03;
		m.m13 = m.m10 * -p.x + m.m11 * -p.y + m.m12 * -p.z; // + m.m13;
		m.m23 = m.m20 * -p.x + m.m21 * -p.y + m.m22 * -p.z; // + m.m23;
		//m.m33 = m.m30 * -p.x + m.m31 * -p.y + m.m32 * -p.z + m.m33;
		m.m33 = 1f;
	}

	[UnityTest]
	public IEnumerator YieldAllocateTest() {

		var prefab = Resources.Load<GameObject>("Test");
		var go = Object.Instantiate(prefab, null);
		var tr = go.transform;
		var view = Matrix4x4.Translate(Vector3.one);
		//UnityEngine.Debug.Log($"{view}");

		yield return null;

		const int iteration = 10000;

		sw.Reset();
		sw.Start();
		for (var i = 0; i < iteration; ++i) {
			//tr.GetPositionAndRotation(out var pos, out var rot);
			view = Matrix4x4.TRS(tr.position, tr.rotation, Vector3.one).inverse;
		}
		sw.Stop();
		UnityEngine.Debug.Log($"{view}");
		UnityEngine.Debug.LogWarning($"TRS+Inverse : {sw.Elapsed}");

		yield return null;

		sw.Reset();
		sw.Start();
		for (var i = 0; i < iteration; ++i) {
			tr.GetPositionAndRotation(out var pos, out var rot);
			TR_Inverse(pos, rot, out view);
		}
		sw.Stop();
		UnityEngine.Debug.Log($"{view}");
		UnityEngine.Debug.LogWarning($"My TRS : {sw.Elapsed}");
	}
}
