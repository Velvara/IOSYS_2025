using System.Collections.Generic;
using System.Linq;
using UnityEngine;

public class TagObjManager : MonoBehaviour
{
    public List<Tags> tags;

    public bool HasTag(Tags tagToCheck)
    {
        return tags.Contains(tagToCheck);
    }

    public void AddTags(Tags addedTag)
    {
        if (addedTag != null && !tags.Contains(addedTag)) tags.Add(addedTag);
    }

    public void ClearTags()
    { 
        tags.Clear();
    }
}
