"""Heading/explicit HTML anchors used by maintained local Markdown links."""
import re

def anchors(body):
    result=set(re.findall(r'<[^>]+\b(?:id|name)=["\x27]([^"\x27]+)["\x27]',body))
    fenced=False
    for line in body.splitlines():
        if re.match(r'^\s*(```|~~~)',line):fenced=not fenced;continue
        if fenced:continue
        heading=re.match(r'^\s{0,3}#{1,6}\s+(.+?)(?:\s+#+)?\s*$',line)
        if not heading:continue
        text=re.sub(r'\[([^]]+)\]\([^)]*\)',r'\1',heading.group(1));text=re.sub(r'<[^>]+>','',text)
        slug=re.sub(r'[^\w\s-]','',text.lower()).strip().replace(' ','-')
        candidate=slug;index=0
        while candidate in result:index+=1;candidate=slug+'-'+str(index)
        result.add(candidate)
    return result
