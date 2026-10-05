using System.Collections;
using NUnit.Framework;
using UnityEngine.TestTools;

public class PlayModeSmokeTests
{
    [UnityTest]
    public IEnumerator PlayModeRunnerWorks()
    {
        yield return null;
        Assert.Pass();
    }
}
