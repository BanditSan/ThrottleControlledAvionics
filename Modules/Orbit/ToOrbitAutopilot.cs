﻿//  Author:
//       Allis Tauri <allista@gmail.com>
//
//  Copyright (c) 2016 Allis Tauri
//
// This work is licensed under the Creative Commons Attribution-ShareAlike 4.0 International License. 
// To view a copy of this license, visit http://creativecommons.org/licenses/by-sa/4.0/ 
// or send a letter to Creative Commons, PO Box 1866, Mountain View, CA 94042, USA.
//
using System;
using UnityEngine;

namespace ThrottleControlledAvionics
{
	[CareerPart]
	[RequireModules(typeof(AttitudeControl),
	                typeof(BearingControl),
					typeof(ThrottleControl),
	                typeof(ManeuverAutopilot))]
	public class ToOrbitAutopilot : TrajectoryCalculator
	{
		public new class Config : ModuleConfig
		{
			[Persistent] public float Dtol           = 100f;
			[Persistent] public float RadiusOffset   = 10000f;
			[Persistent] public float GTurnCurve     = 0.6f;
			[Persistent] public float LaunchTangentK = 1f;
			[Persistent] public float Dist2VelF      = 0.1f;
		}
		static Config ORB { get { return TCAScenario.Globals.ORB; } }

		public enum Stage { None, Start, Liftoff, GravityTurn, ChangeApA, Circularize }

		[Persistent] public FloatField ApA = new FloatField();
		[Persistent] public FloatField Inclination = new FloatField(format: "F3", min: 0, max: 180);
		[Persistent] public bool AscendingNode = true;
		[Persistent] public bool ProgradeOrbit = true;
		[Persistent] public Vector3 Target;
		[Persistent] public Stage stage;

		public bool ShowEditor { get; private set; }

		double ApR { get { return ApA*1000+Body.Radius; } }
		ToOrbitExecutor ToOrbit;

		public ToOrbitAutopilot(ModuleTCA tca) : base(tca) {}

		public override void Save(ConfigNode node)
		{
			if(ToOrbit != null && !ToOrbit.Target.IsZero())
				Target = ToOrbit.Target;
			base.Save(node);
		}

		public override void Init()
		{
			base.Init();
			CFG.AP2.AddHandler(this, Autopilot2.ToOrbit);
		}

		protected override void UpdateState()
		{
			base.UpdateState();
			IsActive &= CFG.AP2[Autopilot2.ToOrbit] && stage != Stage.None;
		}

		public void ToOrbitCallback(Multiplexer.Command cmd)
		{
			switch(cmd)
			{
			case Multiplexer.Command.Resume:
				ToOrbit = new ToOrbitExecutor(TCA);
				ToOrbit.Target = Target;
				break;

			case Multiplexer.Command.On:
				reset();
				Vector3d hVdir;
				if(Inclination.Range > 1e-5f)
				{
					var angle = Utils.Clamp((Inclination.Value-Inclination.Min)/Inclination.Range*180, 0, 180);
					if(!AscendingNode) angle = -angle;
					hVdir = QuaternionD.AngleAxis(angle, VesselOrbit.pos) * Vector3d.Cross(Vector3d.forward, VesselOrbit.pos).normalized;
				}
				else hVdir = Vector3d.Cross(VesselOrbit.pos, Body.orbit.vel).normalized;
				if(!ProgradeOrbit) hVdir *= -1;
				var ascO = AscendingOrbit(Utils.ClampH(ApR, MinPeR+ORB.RadiusOffset), hVdir, ORB.LaunchTangentK);
				Target = ascO.getRelativePositionAtUT(VSL.Physics.UT+ascO.timeToAp);
				stage = Stage.Start;
				goto case Multiplexer.Command.Resume;

			case Multiplexer.Command.Off:
				reset();
				ClearStatus();
				break;
			}
		}

		protected override void reset()
		{
			base.reset();

			ApA.Min = (float)(MinPeR-Body.Radius)/1000;
			ApA.Max = (float)(Body.sphereOfInfluence-Body.Radius)/1000;
			ApA.Value = Utils.Clamp(ApA.Value, ApA.Min, ApA.Max);

			//pos x [fwd x pos] = fwd(pos*pos) - pos(fwd*pos)
			var h = Vector3d.forward*VesselOrbit.pos.sqrMagnitude - VesselOrbit.pos * VesselOrbit.pos.z; 
			Inclination.Min = (float)Math.Acos(h.z/h.magnitude)*Mathf.Rad2Deg;
			Inclination.Max = 180-Inclination.Min;
			Inclination.Value = Utils.Clamp(Inclination.Value, Inclination.Min, Inclination.Max);

			ToOrbit = null;
			Target = Vector3d.zero;
			stage = Stage.None;
		}

		Vector3d correct_dV(Vector3d dV, double UT)
		{
			var v  = VesselOrbit.getOrbitalVelocityAtUT(UT);
			var nV = dV + v;
			return QuaternionD.AngleAxis(VesselOrbit.inclination-Inclination.Value, 
			                             VesselOrbit.getRelativePositionAtUT(UT)) * nV - v;
		}

		void change_ApR(double UT)
		{
			var dV = correct_dV(dV4Ap(VesselOrbit, ApR, UT), UT);
			ManeuverAutopilot.AddNode(VSL, dV, UT);
			CFG.AP1.On(Autopilot1.Maneuver);
			stage = Stage.ChangeApA;
		}

		void circularize(double UT)
		{
			var dV = correct_dV(dV4C(VesselOrbit, hV(UT), UT), UT);
			ManeuverAutopilot.AddNode(VSL, dV, UT);
			CFG.AP1.On(Autopilot1.Maneuver);
			stage = Stage.Circularize;
		}

		protected override void Update()
		{
			if(!IsActive) return;
			switch(stage)
			{
			case Stage.Start:
				stage = VSL.LandedOrSplashed ? Stage.Liftoff : Stage.GravityTurn;
				break;
			case Stage.Liftoff:
				if(ToOrbit.Liftoff()) break;
				stage = Stage.GravityTurn;
				break;
			case Stage.GravityTurn:
				var chord = ToOrbit.Target-VesselOrbit.pos;
				var n = Vector3d.Cross(VesselOrbit.pos, ToOrbit.Target);
				var inclination_error = Math.Acos(n.z/n.magnitude)*Mathf.Rad2Deg - 
					(ProgradeOrbit? Inclination.Value : -Inclination.Value);
				if(!AscendingNode) inclination_error = -inclination_error;
				ToOrbit.Target = QuaternionD.AngleAxis(inclination_error/100, chord)*ToOrbit.Target;
				if(ToOrbit.GravityTurn(ORB.GTurnCurve, ORB.Dist2VelF, ORB.Dtol)) break;
				var UT = VSL.Physics.UT+VesselOrbit.timeToAp;
				if(ApR > MinPeR + ORB.RadiusOffset) change_ApR(UT);
				else circularize(UT);
				break;
			case Stage.ChangeApA:
				Status("Achieving target apoapsis...");
				if(CFG.AP1[Autopilot1.Maneuver]) break;
				circularize(VSL.Physics.UT+VesselOrbit.timeToAp);
				stage = Stage.Circularize;
				break;
			case Stage.Circularize:
				Status("Circularization...");
				if(CFG.AP1[Autopilot1.Maneuver]) break;
				CFG.AP2.Off();
				break;
			}
		}

		public override void Draw()
		{
			#if DEBUG
			if(ToOrbit != null)
			{
				GLUtils.GLVec(Body.position, ToOrbit.Target.xzy, Color.green);
				GLUtils.GLVec(Body.position, VesselOrbit.getRelativePositionAtUT(VSL.Physics.UT+VesselOrbit.timeToAp).xzy, Color.magenta);
				GLUtils.GLVec(Body.position, VesselOrbit.GetOrbitNormal().normalized.xzy*Body.Radius*1.1, Color.cyan);
				GLUtils.GLVec(Body.position, Vector3d.Cross(VesselOrbit.pos, ToOrbit.Target).normalized.xzy*Body.Radius*1.1, Color.red);
			}
			#endif
			if(stage == Stage.None)
			{
				if(Utils.ButtonSwitch("ToOrbit", ShowEditor, 
				                   	  "Achieve a circular orbit with desired radius and inclination", 
				                      GUILayout.ExpandWidth(false)))
				{
					ShowEditor = !ShowEditor;
					if(ShowEditor) reset();
				}
			}
			else if(GUILayout.Button(new GUIContent("ToOrbit", "Change target orbit or abort"), 
			                         Styles.danger_button, GUILayout.ExpandWidth(false)))
				ShowEditor = !ShowEditor;
		}

		public void DrawOrbitEditor()
		{
			GUILayout.BeginVertical();
			GUILayout.BeginHorizontal();
			GUILayout.Label("Radius:", GUILayout.Width(70));
			ApA.Draw("km", true, 5);
			GUILayout.EndHorizontal();
			GUILayout.BeginHorizontal();
			GUILayout.Label("Inclination:", GUILayout.Width(70));
			if(GUILayout.Button(new GUIContent(AscendingNode? "AN" : "DN", "Launch from Ascending or Descending Node?"), 
			                    AscendingNode? Styles.enabled_button : Styles.danger_button,
			                    GUILayout.ExpandWidth(false)))
				AscendingNode = !AscendingNode;
			if(GUILayout.Button(new GUIContent(ProgradeOrbit? "PG" : "RG", "Prograde or retrograde orbit?"), 
			                    ProgradeOrbit? Styles.enabled_button : Styles.danger_button,
			                    GUILayout.ExpandWidth(false)))
				ProgradeOrbit = !ProgradeOrbit;
			Inclination.Draw("°", true, 5);
			GUILayout.EndHorizontal();
			GUILayout.BeginHorizontal();
			ShowEditor = !GUILayout.Button("Cancel", Styles.active_button, GUILayout.ExpandWidth(true));
			if(stage != Stage.None && 
			   GUILayout.Button("Abort", Styles.danger_button, GUILayout.ExpandWidth(true)))
			{
				ShowEditor = false;
				CFG.AP2.XOff();
			}
			if(GUILayout.Button(stage == Stage.None? "Launch" : "Change", 
			                    Styles.confirm_button, GUILayout.ExpandWidth(true)))
			{
				ShowEditor = false;
				CFG.AP2.XOn(Autopilot2.ToOrbit);
			}
			GUILayout.EndHorizontal();
			GUILayout.EndVertical();
		}
	}
}
