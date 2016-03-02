//  Author:
//       Allis Tauri <allista@gmail.com>
//
//  Copyright (c) 2015 Allis Tauri
//
// This work is licensed under the Creative Commons Attribution-ShareAlike 4.0 International License. 
// To view a copy of this license, visit http://creativecommons.org/licenses/by-sa/4.0/ 
// or send a letter to Creative Commons, PO Box 1866, Mountain View, CA 94042, USA.

using System;
using UnityEngine;

namespace ThrottleControlledAvionics
{
	//adapted from MechJeb
	public class WayPoint : ConfigNodeObject, ITargetable
	{
		new public const string NODE_NAME = "WAYPOINT";

		[Persistent] public string Name;
		[Persistent] public double Lat;
		[Persistent] public double Lon;
		[Persistent] public float  Distance;
		[Persistent] public bool   Pause;
		[Persistent] public bool   Land;
		//target proxy
		[Persistent] ProtoTargetInfo TargetInfo = new ProtoTargetInfo();
		ITargetable target;
		//a transform holder for simple lat-lon coordinates on the map
		GameObject go;

		void set_coordinates(double lat, double lon)
		{
			var c = new Coordinates(lat, lon);
			Lat = c.Lat; Lon = c.Lon;
		}

		void set_coordinates(Vessel v)
		{ set_coordinates(v.latitude, v.longitude); }

		public override string ToString() { return string.Format("[{0}] {1}", GetName(), new Coordinates(Lat, Lon)); }

		public WayPoint() { Distance = TCAScenario.Globals.PN.MinDistance; }
		public WayPoint(Coordinates c) : this() { Lat = c.Lat; Lon = c.Lon; Name = c.ToString(); go = new GameObject(); }
		public WayPoint(ITargetable t) : this() { target = t; TargetInfo = new ProtoTargetInfo(t); Name = t.GetName(); }
		public WayPoint(double lat, double lon) : this(new Coordinates(lat,lon)) {}

		//using Haversine formula (see http://www.movable-type.co.uk/scripts/latlong.html)
		public double AngleTo(double lat, double lon)
		{
			var lat1 = Lat*Mathf.Deg2Rad;
			var lad2 = lat*Mathf.Deg2Rad;
			var dlat = lad2-lat1;
			var dlon = (lon-Lon)*Mathf.Deg2Rad;
			var a = (1-Math.Cos(dlat))/2 + Math.Cos(lat1)*Math.Cos(lad2)*(1-Math.Cos(dlon))/2;
			return 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1-a));
		}
		public double AngleTo(Coordinates c) { return AngleTo(c.Lat, c.Lon); }
		public double AngleTo(WayPoint wp) { return AngleTo(wp.Lat, wp.Lon); }
		public double AngleTo(Vessel vsl) { return AngleTo(vsl.latitude, vsl.longitude); }
		public double DistanceTo(Vessel vsl) { return AngleTo(vsl)*vsl.mainBody.Radius; }
		public double SurfaceAlt(CelestialBody body) { return Utils.TerrainAltitude(body, Lat, Lon); }

		public static double BearingTo(double lat1, double lat2, double dlon)
		{
			var cos_lat2 = Math.Cos(lat2);
			var y = Math.Sin(dlon)*cos_lat2;
			var x = Math.Cos(lat1)*Math.Sin(lat2) - Math.Sin(lat1)*cos_lat2*Math.Cos(dlon);
			return Math.Atan2(y, x);
		}
		public double BearingFrom(Vessel vsl)
		{ return BearingTo(vsl.latitude*Mathf.Deg2Rad, Lat*Mathf.Deg2Rad, (Lon-vsl.longitude)*Mathf.Deg2Rad);	}


		public static Coordinates PointBetween(double lat1, double lon1, double lat2, double lon2, double dist)
		{
			lat1 = lat1*Mathf.Deg2Rad;
			//bearing
			var bearing = BearingTo(lat1, lat2*Mathf.Deg2Rad, (lon2-lon1)*Mathf.Deg2Rad);
			//trigs
			var sin_d    = Math.Sin(dist);
			var cos_d    = Math.Cos(dist);
			var sin_lat1 = Math.Sin(lat1);
			var cos_lat1 = Math.Cos(lat1);
			//result
			var lat  = Math.Asin(sin_lat1*Math.Cos(dist) + cos_lat1*sin_d*Math.Cos(bearing));
			var dlon = Math.Atan2(Math.Sin(bearing)*sin_d*cos_lat1, cos_d-sin_lat1*Math.Sin(lat));
			return new Coordinates(lat*Mathf.Rad2Deg, lon1+dlon*Mathf.Rad2Deg);
		}
		public Coordinates PointBetween(double lat, double lon, double dist) { return PointBetween(Lat, Lon, lat, lon, dist); }
		public Coordinates PointBetween(WayPoint wp, double dist) { return PointBetween(Lat, Lon, wp.Lat, wp.Lon, dist); }
		public Coordinates PointFrom(Vessel v, double dist) { return PointBetween(v.latitude, v.longitude, Lat, Lon, dist); }

		//Call this every frame to make sure the target transform stays up to date
		public void Update(CelestialBody body) 
		{ 
			if(target != null) UpdateCoordinates(body);
			if(TargetInfo.targetType != ProtoTargetInfo.Type.Null && 
			   HighLogic.LoadedSceneIsFlight)
			{
				target = TargetInfo.FindTarget();
				if(target == null) 
				{
					TargetInfo.targetType = ProtoTargetInfo.Type.Null;
					Name += " last location";
				}
				else UpdateCoordinates(body);
			}
			else
			{
				if(go == null) go = new GameObject();
				go.transform.position = body.GetWorldSurfacePosition(Lat, Lon, Utils.TerrainAltitude(body, Lat, Lon)); 
			}
		}

		public void UpdateCoordinates(CelestialBody body)
		{
			if(target == null) return;
			Name = target.GetName();
			switch(TargetInfo.targetType)
			{
			case ProtoTargetInfo.Type.Vessel:
				var v = target as Vessel;
				if(v == null) break;
				set_coordinates(v);
				return;
			case ProtoTargetInfo.Type.PartModule:
				var m = target as PartModule;
				if(m == null || m.vessel == null) break;
				set_coordinates(m.vessel);
				return;
			case ProtoTargetInfo.Type.Part:
				var p = target as Part;
				if(p == null || p.vessel == null) break;
				set_coordinates(p.vessel);
				return;
			case ProtoTargetInfo.Type.Generic:
				var t = target.GetTransform();
				if(t == null) break;
				set_coordinates(body.GetLatitude(t.position),
				                body.GetLongitude(t.position));
				return;
//			case ProtoTargetInfo.Type.CelestialBody:
			default:
				break;
			}
			TargetInfo.targetType = ProtoTargetInfo.Type.Null;
			Name += " last location";
			target = null;
		}

		public string GetName() { return Name; }
		public ITargetable GetTarget() { return target ?? this; }
		public Vector3 GetFwdVector() { return target == null? Vector3.up : target.GetFwdVector(); }
		public Vector3 GetObtVelocity() { return target == null? Vector3.zero : target.GetObtVelocity(); }
		public Orbit GetOrbit() { return target == null? null : target.GetOrbit(); }
		public OrbitDriver GetOrbitDriver() { return target == null? null : target.GetOrbitDriver(); }
		public Vector3 GetSrfVelocity() { return target == null? Vector3.zero : target.GetSrfVelocity(); }
		public Vessel GetVessel() { return target == null? null : target.GetVessel(); }
		public VesselTargetModes GetTargetingMode() { return target == null? VesselTargetModes.Direction : target.GetTargetingMode(); }

		public Transform GetTransform() 
		{ 
			if(target == null)
			{
				if(go == null) go = new GameObject();
				return go.transform; 
			}
			return target.GetTransform(); 
		}
	}
}

