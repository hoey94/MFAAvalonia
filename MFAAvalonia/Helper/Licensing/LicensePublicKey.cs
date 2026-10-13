namespace MFAAvalonia.Helper.Licensing;

// Public verification key only. The matching private key is delivered separately to the owner.
// Never add the private key to the repository, application assets, logs, or release packages.
internal static class LicensePublicKey
{
    internal const string Pem = """
-----BEGIN PUBLIC KEY-----
MIIBojANBgkqhkiG9w0BAQEFAAOCAY8AMIIBigKCAYEA1yFF8tWmsBBT2cGVpCSt
QLLmt2Mg+mo9sHGdgopLdF+/pd++b0k7ShVHS5s9Bd27ufuzzBBt2aO2lBUcKZSs
hOO3fOKlseaRF5gRYe+UgA0uJdpQX+tHkLkacty7yCK6VecCZ6fZqsR8+S6OaPHu
021BaGwXhhg4rqgSFv5O3JkiRkQGk1B/qExEgKP2MaqVBmuoYRflfw2EbrB9t77n
gHR5u4XJHcJI8i+y4dCsbyUzXloJXN2IgBXZifA0gP2buRgQTKRtvVy4oAYPjhhS
2uCvuf9D7BCZjIHz4upN3s1o5Zc5e95iBy+aRHMN/EStxEw+vHF6ofYxzQTj4UEf
8r6LUWgIZL05GCI9lr/01ZKTZeE+x035tPypoA8Cz7dUFvcJeF1LmkBqzfJ0j0oz
Hq2jkSpWCkGBu/TC5JvO8zLBbsjC1u6zpFwKyE33izJAJaV+76dDuY+FPe2HnWYU
VGhc5FkLWVUZkuVCIq0Am/Mg2JmGt8+81jgEXFuqbcapAgMBAAE=
-----END PUBLIC KEY-----
""";
}
