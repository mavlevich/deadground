"""Optional raster preview. Validation, SVG and HTML need only Python's standard library."""
import math

from ustye_plan import contour_segments


def render_png(plan, destination):
    import matplotlib
    matplotlib.use('Agg')
    import matplotlib.pyplot as plt
    from matplotlib.patches import Polygon

    def project(point):
        x, z = point
        return (x-z)/1000/math.sqrt(2), (5000-x-z)/1000/math.sqrt(2)

    fig, ax = plt.subplots(figsize=(11, 12), dpi=140)
    fig.patch.set_facecolor('#f4f1e8')
    outline = [project(p) for p in [[0,0],[5000,0],[5000,5000],[0,5000]]]
    clip = Polygon(outline, transform=ax.transData)
    ax.add_patch(Polygon(outline, facecolor='#e4dfc6', edgecolor='#59614f', linewidth=1))
    colors = {'dense_forest':'#819573', 'sparse_forest':'#b1c1a0', 'urban':'#c1bcb2',
              'industrial':'#c4ad83', 'candidate_dead_ground':'#d4d4b1'}
    for region in plan['regions']:
        ax.add_patch(Polygon([project(p) for p in region['polygon']], facecolor=colors[region['kind']],
                             edgecolor='#718166', linewidth=.5))
    for level, segment in contour_segments(plan):
        x, y = zip(*[project(p) for p in segment])
        ax.plot(x,y,color='#978a66',linewidth=.6,alpha=.6)
    ax.add_patch(Polygon([project(p) for p in plan['water']['bay']],facecolor='#a8c7cf',edgecolor='none'))
    x,y=zip(*[project(p) for p in plan['water']['river']])
    river=ax.plot(x,y,color='#91b7c4',linewidth=12,solid_capstyle='butt')[0]
    river.set_clip_path(clip)
    for road in plan['roads'] + plan['access_roads']:
        x,y=zip(*[project(p) for p in road['points']])
        if road['mode']=='vehicle':
            ax.plot(x,y,color='#f9f3df',linewidth=4)
            ax.plot(x,y,color='#7c7868',linewidth=1.5)
        else:ax.plot(x,y,color='#9b5142',linewidth=2,linestyle=':')
    for structure in plan['structures']:
        x,z=structure['min'];w,h=structure['size']
        ax.add_patch(Polygon([project(p) for p in [[x,z],[x+w,z],[x+w,z+h],[x,z+h]]],
                             facecolor='#9a9c96' if structure['kind']=='panel_block' else '#ae9271',
                             edgecolor='#535b4c',linewidth=.5))
    for deploy in plan['deployments']:
        ax.add_patch(Polygon([project(p) for p in deploy['polygon']],fill=False,edgecolor='#716f67',linestyle='--',linewidth=.8))
    for site in plan['sites']:
        x,y=project(site['position'])
        color='#557c91' if site['label']=='A' else '#9f6254' if site['label']=='B' else '#f4f1e8'
        ax.scatter([x],[y],s=190,c=color,edgecolors='#394c3e',zorder=10)
        ax.text(x,y,site['label'],ha='center',va='center',fontsize=9,fontweight='bold',
                color='white' if site['kind']=='spawn' else '#30382e',zorder=11)
        if 'caption' in site:
            dx,dy=site['caption_offset_px']
            ax.annotate(site['caption'],(x,y),xytext=(dx,-dy),textcoords='offset points',
                        ha='right' if dx<0 else 'left',va='center',fontsize=8,color='#30382e',
                        bbox={'facecolor':'#f4f1e8','edgecolor':'none','alpha':.85,'pad':1.5},zorder=12)
    ax.text(0,4.04,'USTYE: BACKWATER CROSSING',ha='center',fontsize=19,fontweight='bold',color='#30382e')
    ax.text(0,3.8,'Fictional design | 5000 x 5000 m | 25 km2 | north = -Z',ha='center',fontsize=10,color='#30382e')
    ax.annotate('N',(2.95,3.15),(2.5,2.7),arrowprops={'arrowstyle':'->','color':'#30382e'},color='#30382e')
    ax.plot([-3.3,-2.3],[-3.7,-3.7],color='#30382e',linewidth=2)
    ax.text(-2.8,-3.57,'1 km',ha='center',fontsize=9,color='#30382e')
    for y,text in [(-3.95,'1 north crossing | 2 central bridge | 3 south crossing | 4 church | 5 depot'),
                   (-4.15,'6 riverside town | 7 radio hill | 8 lighthouse | 9 dismount | 10 sawmill | 11 water tower'),
                   (-4.38,'Design only. No runtime navigation, visibility, supply or destruction implementation.')]:
        ax.text(0,y,text,ha='center',fontsize=9,color='#30382e')
    ax.set_xlim(-3.9,3.9);ax.set_ylim(-4.55,4.2);ax.set_aspect('equal');ax.axis('off')
    fig.savefig(destination,bbox_inches='tight',facecolor=fig.get_facecolor(),metadata={'Software':'Dead Ground map authoring'})
    plt.close(fig)
