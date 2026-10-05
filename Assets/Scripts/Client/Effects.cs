using DG.Tweening;
using UnityEngine;

/// <summary>Client-only placeholder visual effects built from procedural sprites.</summary>
public static class Effects
{
    private const int SortingOrder = 100;

    public static void Explosion(Vector2 position)
    {
        var go = new GameObject("Explosion");
        go.transform.position = new Vector3(position.x, position.y, 0f);
        go.transform.localScale = Vector3.one * 0.2f;
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = ProceduralSprites.Circle;
        sr.color = new Color(1f, 0.6f, 0.1f, 1f);
        sr.sortingOrder = SortingOrder;

        DOTween.Sequence()
            .Join(go.transform.DOScale(1.6f, 0.35f).SetEase(Ease.OutQuad))
            .Join(DOTween.To(() => sr.color.a, a => sr.color = new Color(sr.color.r, sr.color.g, sr.color.b, a), 0f, 0.35f))
            .OnComplete(() => Object.Destroy(go));
    }

    public static void Tracer(Vector3 from, Vector3 to)
    {
        var go = new GameObject("Tracer");
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = ProceduralSprites.Pixel;
        sr.color = Color.yellow;
        sr.sortingOrder = SortingOrder;
        go.transform.position = from;
        go.transform.right = (to - from).normalized;
        go.transform.localScale = new Vector3(0.5f, 0.1f, 1f);
        go.transform.DOMove(to, 0.2f).SetEase(Ease.Linear).OnComplete(() => Object.Destroy(go));
    }
}
