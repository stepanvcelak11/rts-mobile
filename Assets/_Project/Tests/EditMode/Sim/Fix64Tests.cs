using NUnit.Framework;
using RTS.Sim.Core;

namespace RTS.Tests
{
    public class Fix64Tests
    {
        [Test]
        public void FromDecimal_RoundTrips_ThreeDecimals()
        {
            Assert.AreEqual(4.5m, Fix64.FromDecimal(4.5m).ToDecimal());
            Assert.AreEqual(0.67m, decimal.Round(Fix64.FromDecimal(0.67m).ToDecimal(), 3));
            Assert.AreEqual(-3.25m, Fix64.FromDecimal(-3.25m).ToDecimal());
            Assert.AreEqual(Fix64.FromInt(7), Fix64.Parse("7"));
        }

        [Test]
        public void Arithmetic_IsExactForRepresentableValues()
        {
            Fix64 a = Fix64.FromDecimal(1.5m), b = Fix64.FromDecimal(2.25m);
            Assert.AreEqual(3.75m, (a + b).ToDecimal());
            Assert.AreEqual(-0.75m, (a - b).ToDecimal());
            Assert.AreEqual(3.375m, (a * b).ToDecimal());
            Assert.AreEqual(1.5m, (Fix64.FromDecimal(3.375m) / b).ToDecimal());
            Assert.AreEqual(Fix64.FromInt(-6), Fix64.FromInt(3) * Fix64.FromInt(-2));
            Assert.AreEqual(Fix64.FromInt(-2), Fix64.FromInt(-6) / Fix64.FromInt(3));
        }

        [Test]
        public void Multiplication_HandlesLargeAndSmall()
        {
            Fix64 big = Fix64.FromInt(30000);
            Assert.AreEqual(900000000m, (big * big).ToDecimal());
            Fix64 tiny = Fix64.Ratio(1, 1024);
            Assert.AreEqual(1m / (1024m * 1024m), (tiny * tiny).ToDecimal());
        }

        [Test]
        public void Sqrt_MatchesKnownValues()
        {
            Assert.AreEqual(Fix64.FromInt(4), FixMath.Sqrt(Fix64.FromInt(16)));
            Assert.AreEqual(Fix64.FromDecimal(1.5m), FixMath.Sqrt(Fix64.FromDecimal(2.25m)));
            Assert.AreEqual(Fix64.Zero, FixMath.Sqrt(Fix64.Zero));
            decimal r2 = FixMath.Sqrt(Fix64.FromInt(2)).ToDecimal();
            Assert.That(r2, Is.EqualTo(1.41421356m).Within(0.000001m));
            decimal large = FixMath.Sqrt(Fix64.FromInt(1000000)).ToDecimal();
            Assert.That(large, Is.EqualTo(1000m).Within(0.000001m));
        }

        [Test]
        public void Rounding_Works()
        {
            Assert.AreEqual(2, Fix64.FromDecimal(2.7m).FloorToInt());
            Assert.AreEqual(-3, Fix64.FromDecimal(-2.3m).FloorToInt());
            Assert.AreEqual(3, Fix64.FromDecimal(2.5m).RoundToInt());
            Assert.AreEqual(3, Fix64.FromDecimal(2.1m).CeilToInt());
            Assert.AreEqual(2, Fix64.FromInt(2).CeilToInt());
        }

        [Test]
        public void Vec2_NormalizeAndDistance()
        {
            var v = new FixVec2(Fix64.FromInt(3), Fix64.FromInt(4));
            Assert.AreEqual(Fix64.FromInt(5), v.Length);
            FixVec2 n = v.Normalized;
            Assert.That(n.Length.ToDecimal(), Is.EqualTo(1m).Within(0.000001m));
            Assert.AreEqual(FixVec2.Zero, FixVec2.Zero.Normalized);
            Assert.AreEqual(Fix64.FromInt(25), FixVec2.DistanceSq(FixVec2.Zero, v));
        }

        [Test]
        public void DetRandom_IsDeterministicPerSeed()
        {
            var a = new DetRandom(42);
            var b = new DetRandom(42);
            var c = new DetRandom(43);
            bool differs = false;
            for (int i = 0; i < 1000; i++)
            {
                uint x = a.NextUInt(), y = b.NextUInt();
                Assert.AreEqual(x, y);
                if (x != c.NextUInt()) differs = true;
            }
            Assert.IsTrue(differs);
            for (int i = 0; i < 1000; i++)
            {
                int r = a.Range(3, 7);
                Assert.That(r, Is.InRange(3, 6));
                Fix64 f = a.NextFix01();
                Assert.IsTrue(f >= Fix64.Zero && f < Fix64.One);
            }
        }

        [Test]
        public void SecondsToTicks_RoundsUp()
        {
            Assert.AreEqual(20, SimConstants.SecondsToTicks(1m));
            Assert.AreEqual(30, SimConstants.SecondsToTicks(1.5m));
            Assert.AreEqual(3, SimConstants.SecondsToTicks(0.11m));
            Assert.AreEqual(0, SimConstants.SecondsToTicks(0m));
        }
    }
}
