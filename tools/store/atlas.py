"""Reads the generated sprite atlases in Assets/Resources/Art (y counted from the bottom) and tints team parts."""
import json, os, numpy as np
from PIL import Image
ART=os.path.join(os.path.dirname(os.path.abspath(__file__)),'..','..','Assets','Resources','Art')+os.sep
class Atlas:
    def __init__(s,name):
        s.name=name
        s.d=json.load(open(ART+name+'.json'))
        s.img=np.array(Image.open(ART+name+'.png').convert('RGBA')).astype(np.float32)/255
        s.mask=np.array(Image.open(ART+name+'_mask.png').convert('RGBA'))[...,0].astype(np.float32)/255
        s.H=s.img.shape[0]
        s.by={x['id']:x for x in s.d['sprites']}
        m=s.mask>0.5
        s.gain=1/np.median(s.img[...,0][m]) if m.any() else 1
    def cell(s,id,frame=0,direction=0):
        sp=s.by[id]; return sp['first']+direction*sp['frames']+frame
    def get(s,id,frame=0,direction=0,team=None,flip=True):
        f=s.d['frames'][s.cell(id,frame,direction)]
        x,y,w,h=f['x'],f['y'],f['w'],f['h']
        y0 = s.H-y-h if flip else y
        c=s.img[y0:y0+h,x:x+w].copy(); m=s.mask[y0:y0+h,x:x+w]
        if team is not None:
            t=np.array([int(team[i:i+2],16)/255 for i in (0,2,4)])
            tinted=np.clip(c[...,:3]*t*s.gain,0,1)
            c[...,:3]=c[...,:3]*(1-m[...,None])+tinted*m[...,None]
        return Image.fromarray((c*255).astype(np.uint8),'RGBA')
