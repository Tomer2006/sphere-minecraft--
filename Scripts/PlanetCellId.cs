using System;

namespace SphereMinecraft;

public readonly struct PlanetCellId : IEquatable<PlanetCellId>
{
	public PlanetCellId(int face, int radius, int u, int v)
	{
		Face = face;
		Radius = radius;
		U = u;
		V = v;
	}

	public int Face { get; }

	public int Radius { get; }

	public int U { get; }

	public int V { get; }

	public bool Equals(PlanetCellId other)
	{
		return Face == other.Face &&
			   Radius == other.Radius &&
			   U == other.U &&
			   V == other.V;
	}

	public override bool Equals(object? obj)
	{
		return obj is PlanetCellId other && Equals(other);
	}

	public override int GetHashCode()
	{
		return HashCode.Combine(Face, Radius, U, V);
	}

	public static bool operator ==(PlanetCellId left, PlanetCellId right)
	{
		return left.Equals(right);
	}

	public static bool operator !=(PlanetCellId left, PlanetCellId right)
	{
		return !left.Equals(right);
	}

	public override string ToString()
	{
		return $"Face {Face} R {Radius} U {U} V {V}";
	}
}
