// A harmless, deliberately non-ARK process to verify Manager attachment without touching production hosts.
Console.WriteLine("Fake ASA process ready: "+string.Join(" ",args));
while(true) Thread.Sleep(500);
