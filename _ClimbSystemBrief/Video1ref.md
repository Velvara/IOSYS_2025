[Music]
all right can you hear me so I'm David
from Wolfire games I've been making
games in my spare time for about 20
years and recently we're probably best
known for starting a distribution
platform called Humble Bundle but I'm
not going to talk about that today right
Overgrowth
now I'm working on a game called
overgrowth and we have a pretty small
team it's just myself and an artist
that's twice as big as any team that
I've worked in before but I'm still
doing all the animation and the
programming so I really had to try to
use my time very efficiently when making
this game so I tried to abstract
whatever animation tasks they could from
keyframe work into curves and other
overlapping systems so I'll talk about
how I did that for movement with
movement we've always had kind of a
trade-off between responsive movement
and detailed animation like on one
extreme we have a game like Mario where
a controller input is mapped directly
onto acceleration of the character and
the animation is very simple like you
probably has like two frames running one
frame for jumping down the other extreme
we have like rotoscoped animation like
in prince of persia where all of the
animations transitions smoothly from one
to another and during these transitions
controller input is more or less ignored
so it looks really smooth but it adds a
bit of latency
for a controller input so nice thinking
of trying to get both responsive and
detailed controls one thing i looked at
is vehicle movement like the warthog and
halo when it does have stopped there
it's still responding very quickly to
all the controller input but it also has
all this fancy animation like the shocks
on the wheels
secondary movement on the antenna and so
on so I want to think about how to apply
this to a human character so it seems
like a human would be a very complicated
vehicle because we have like 40
animation bones moving around and
twisting but really if we think about it
if you look at his center of mass
it's moving in a very simple way it's
just making these simple curves around
the ice and not moving up and down very
much at all except when he jumps it goes
in a little gravity parabola there are
only a few rules he has to always follow
like he always has to keep his center of
mass over his skates so he doesn't fall
over and he has to tilt into
acceleration so that that'll be true in
the next second so then I second to the
center of mass will still be over his
skates if he has to conserve his angular
momentum whenever he wants to spin
please can start spinning and then he'll
compact into a line to spin really fast
or I'll spread out to spin more slowly
the same is true with this gymnast doing
floor exercises like she seems very
complicated but she's really just like
animation 101 just a bouncing ball she's
bouncing once once and then a big one
and then she compresses a little bit on
the landing with some squash and stretch
and there's always a constant gravity
like she's always accelerating downwards
at nine point one eight or nine point
eight one meters per second squared
whenever she's not in contact with the
ground that's true if she's jumping
flipping running or in the air in any
way so the overgrowth I wanted to start
by focusing on the basics physics
movement like start with and Mario like
controller input equals acceleration and
wrap everything around there and never
interfere with it so I had this one
green sphere as a bumper sphere it lets
him fly it off objects that he runs into
you hit a weightlifters sphere which
moves up over small obstacles and this
by using this as a foundation for all
the animation we always had really
consistent controls like whenever the
player presses forwards are right it
does something predictable so if they
fall off a cliff they know about it's
their fault
and not the game's fault and whatever
it's so predictable they describe it
using words like intuitive and
responsive which is a much better thing
to see in reviews of your game than like
awkward or sluggish or floaty so I kind
of feel like there should be a
Hippocratic oath for game animation
which is at first you do no harm to the
game
play so by making sure we have the
movement in first and we do the
animation on top of that we can make
sure that that's the case so the first
step was just to drape an idle pose onto
our physics fear so here he just floats
around and he rotates towards his
velocity and his rotation is reactive to
the velocity it's not the direction the
controller is pushing it's the direction
he's actually going so if he moves along
the wall he kind of slides to face in
the direction he's going instead of kind
of running into the wall the next step
was to add some acceleration tilt to it
so when you whenever he accelerates it
tilts in that direction that already
makes it kind of fun to move around like
he starts to move around a bit like a
Segway or some other simple vehicle but
I'm not making an ice skating game so I
wanted him to actually move on the
Movement
ground so I just added these two
keyframes there's the pass pose and the
reach pose where his legs are passing
and then the extreme position and it
just kind of he's the surveyor
wheel-like technique just to figure out
how much distance he's moved on the
ground and that kind of takes off the
keyframes in the animation so no matter
how fast or how slow he's moving
he never floats and his footsteps it
destroys and sync with the ground then I
tried adding another speed for walking
the walk has much smaller stride so it
has to have a much smaller wheel just to
take off each one and it's a bit jarring
to suddenly transition from one to the
other so I added a synchronized blend
between them so at any intermediate
speed it was blend between the two
keyframes and also blend between their
stride size so I can go at fast slow or
any intermediate level and since I was
just keyframing the shape the shape of
his limbs around the center of mass I
had to add in a bit of a bounce so you
could have a bouncy jog when he's moving
slowly and it gets flatter the faster he
goes just because gravity is always
constant if you have half as half as
much time between each
you can only fall a quarter as much
distance just by doing out the
integration two keyframes is not nearly
enough to just work from one to the next
so I had to start interpolating from one
angle to the next just by doing a
weighted average between the two nearest
frames and that helps preserve spatial
continuity so it goes from one frame to
the next but there's a sudden velocity
jar at the end his arms reach forwards
and suddenly start going backwards so
I've created that to by cubic
interpolation which makes sure there's
spatial and velocity continuity and that
made the I thought the run even in slow
motion looked pretty acceptable even
with only two keyframes then I needed
some squash for the compression like we
saw with a gymnast decided a Crouch
frame we can interpolate linearly
linearly there but organic creatures
almost always like that with linear
interpolation and now animator whatever
use linear interpolation just for their
own internal animation but it's also
important to avoid it when transitioning
from one to another so I tried using a
spring damper system to interpolate
between these two keyframes which just
has these two parameters the stiffness
of the spring and the damping factor I
just tweaked that into a looked right
and that adds a lot of the nice easing
in and follow-through that I would have
animated if I wanted to do like a
transition animation by hand but since
the curve is separated from the
transition itself it can work with any
standing pose in any crouching pose or
even with a standing animation and a
crouching animation so decided another
synchronized locomotion for Crouch
walking so now we have our six
locomotion keyframes and our two
standing keyframes and since we're using
a spring damper system I could use the
existing Crouch to handle absorbing
landings it just adds some downwards
force to the crouching spring so we
don't really need to land
animation at all and since this
transition is separated also it will
work while he's running or while he's
standing still next I wanted to add some
ways for players to express themselves
while they're in the air so they could
flip in any direction by just pressing a
controller direction and pressing the
flip button with the linear rotation as
always it looks pretty bad especially at
the end there we certainly like where
this is angular momentum go just kind of
disappears where does it come from in
the first place for that matter so I
tried to dressing that a little bit by
changing the curve this is a little bit
of acceleration or anticipation at the
beginning so he's still kind of
manufacturing momentum from nowhere but
it looks a lot more plausible and then
at the end the he is out is synchronized
with his transition back into his jump
pose so it works more like it works for
the figure skater like he's rotating
really fast boys balled up and then he
slows down as he expands outwards and he
used the same system for a roll we just
rotates around the center of mass with a
rolling pose but he has the different
tuck pose for rolling forwards and
rolling to the side and interpolates
between them for any intermediate angle
so he's always kind of rolling over his
shoulders instead of rolling over his
spine or his head those are all really
simple systems but when you put them all
together it gets pretty compelling
results I think even with only 13
keyframes pretty it transitions between
every possible thing you might do pretty
well like we have a simple system for
all standing and crouching transitions
using that spring damper system we have
an acceleration tilt for all horizontal
movement and we have all these rolling
and flipping systems with controlled
curves and the nice thing about using so
few keyframes is that it's not that hard
to add a whole new variation like if I
want to make a variation where he's
carrying a spear I don't have to like
layer a spear pose on top I could just
make 13 new poses that take this beer
into account so he'll roll nicely
without like
trading the ground once we have as
Refinement
animations we're happy with there are a
lot of procedural ways to refine them
like we've already gone over in risk
kinematics a little bit where you just
move the foot or the hand and
automatically calculate the angles for
the joints we don't really need a
library for that like it's kind of
pretty simple trigonometry just to
figure out that angle for a2 to bone I K
there can take that a little bit farther
for really tricky situations like this
ledge climb I just had the one keyframe
for grabbing onto a ledge and then
constructed all his movement using in
brisk and medics so you can shimmy along
using big shimmies or small ones or
whatever angle you can go up and down so
I just couldn't it seemed easier than
making a hundred different variations in
blender
which is what I'm using for animation in
this case we can all see you semi K for
just look targets so I use the multiple
joint I K for the hands and feet to make
sure that all the contexts are preserved
but this helps with more like social
contacts so enemies your characters
always looking at their targets are
looking in the direction of the camera
or facing they can face their torso one
way and their head another way it just
helps make the characters look a lot
more alive and aware of what's going on
it's pretty common now also to add
secondary physics to the character
somehow like by adding a cape or by
adding like wobbly scabbard or in this
case is wall blue ears but I wanted to
try bringing that a little bit deeper
into the character so every animation
has sort of a softness softness
parameter for each bone in this case his
arms are a little bit soft
she kind of wobbles when he runs around
and secondary physics are nice to bring
as much of that in as possible because
it helps transition not only between
different animations but different
rotations and different velocities and
any kind of change
and finally for refinement I tried to
address it kind of like a profiling
problem like I just tried different
things find what looks the stupidest so
in this case it kind of like stupid to
slide along the wall on this face like
that because it kind of exposes this
like invisible collision sphere so I
tried just using my locomotion system to
replace that with a simple wall run
animation and I found that took one of
the stupidest looking situations and
made it one of the cooler ones that
everyone likes to post screenshots have
on Steam in almost every game now uses
rag dolls for destroyed characters they
first saw them and Carmageddon to where
they just use simple box physics to
allow you to collide with pedestrians
and pile drives them into walls or like
knock one pedestrian into another one
Ragdolls
and make them both fall over but
I didn't see articulated ragdolls until
hitman codename 47 which kind of blew my
mind at the time because you could drag
on the bodies by any limb that you want
to you or you could bless them up
against walls and they kind of slide
down in a dramatic way and since he
wrote a paper about it the guy who did
this immediately wanted to try it for a
game jam project series one where you're
a psychic bodyguard where you have to
protect the VIP and the white outfit by
shooting all these guys so the instant
ragdoll transition worked well for
extreme situations like you blow their
limbs off with a grenade or something
but in movies they're often different
kinds of death animations that are more
dramatic and drawn out like this guy is
kind of following slowly like a tree or
someone's clutching their shoulder when
they get shot or even sometimes they
just acted out by standing up for a
while so the hero can just shoot them
dramatically and satisfyingly many times
like this poor guy here so I try just
delaying the ragdoll a little bit to
allow the characters to have a bit more
time to react to what's going on it's
like if you shoot them in the leg they
might grab there like and if you shoot
them again then they'll go into an
immediate rag doll or they'll stay up
for a little while she can just unload
your machine gun into them and it even
added a little bit to the gameplay
because sometimes you'll shoot someone
and they're dying but they're still
about to kill your VIP so you have to
keep shooting them which is very violent
now that I think about it okay it did
add to the game I think the next big
thing in rag dolls
Rockstar announced they're using this
technology for eeeh Ted active ragdolls
so it's some kind of AI for the joint
forces so not totally limp they're still
trying to do something like grab onto
things or sort of keep their balance so
try doing something like that in over go
and started with pose matching so he'll
just try and preserve whatever pose he
was in by applying joint constraints and
that was pretty fun to do the next step
was to add animation matching so now
he's playing is his walk animation but
he's not really walking very well and
finally I extended that to like an
actual useful system so when he's far
away from a surface he'll flail with
these three flailing keyframes and when
he gets close to the surface will kind
of curl up and try and protect himself
and if there's a surface coming to the
front he'll put his arms out forwards it
doesn't help very much but it makes him
look a lot more alive unless like he is
just unplugged from the matrix a lot of
the time when you punch someone they'll
fall over and I don't want them to keep
looking like they're dying getting
resurrected the same kind of techniques
First-person
can be applied to anything any kind of
animation not just third-person
characters like in this case I made a
game jam project called receiver which
is all about really detailed gun
manipulation it's instead of our for
reload it's like our is rack the slide
and he is like eject magazine so I did
the fight divide it all up into little
stages like this and linear
interpolation continues to look bad so I
tried using the spring interpolation
again and that already I thought looked
pretty natural like even with just two
or three keyframes like that's more like
how someone's arm might move around I
still reacts instantly at any controls
like you can interrupt any any of those
transitions at any time but we still
only need three keyframes for that
it's a similar kind of rotational spring
for the gun which I use for any kind of
sudden impacts like there's a gun recoil
of course but I also used it for
footsteps and even for like taking out a
magazine and slamming it back in just
helped accent the movements a little bit
and receiver I had to decompose
everything into little movements like
this because that was just part of the
gameplay design but I feel like I would
do it even if you just press Start to
reload like usual because it made it
really easy to make variations each time
like when you eject the bullets one of
them might get stuck so you have to do
it multiple times or you might not fill
it completely or you could spin it a
little bit while putting it back and
having it in little pieces like that
could make it really easy to randomly
add variety to all reloads every time
there a bunch of good examples of games
that I didn't work on that do a pretty
cool job with procedural animation like
Shadow of the Colossus had a remarkable
animation problem because they had one
skin character climbing around on the
bigger skin character which i think has
not really been attempted since then so
usually that has just handled by
rotating the character and using inverse
kinematics to make sure the hands and
feet lined up but in extreme cases he'll
lose his grip entirely then he's
simulated with a sort of two part
pendulum like one part from his hand to
his chest one part from his chest to his
legs so it kind of flaps around and that
simulation is applies to the pre-made
flopping around animation just to make
sure it all lines up a 2d example is
Rain World
this upcoming game called reign world
which has this really cool little slug
cat character which i think is what they
call it whenever he runs he tilts in the
direction he's going kind of like the
figure skater or what I did for
overgrowth always crawling like even
seeing a little window on the left he
moves like the character and snake-like
has his front part is kind of dragging
his rear part behind it and he's inverse
kinematics to make sure his limbs all
hooked up to the nearest surface
and he always has secondary motion on
the tail the tail is always physically
simulated so it has nice smooth
transitions between every state the more
Gang Beasts Always active ragdoll
extreme examples this game called gang
beasts which is always in an active
ragdoll state so you can have these
crazy situations like little guy trying
to climb on the big guy and pummel him
in the head and they didn't have to
simulate it in great detail they use an
invisible sphere to kind of prop the guy
up but it's really neat how it allowed
for such difficult situations
environment interaction you don't have
to take it quite this far you could
always do like a hybrid so sometimes
they're active rag doll and sometimes
they're moving around normally I saw
this is a great case study so in the
future I think we really need animation
and code to work more closely together
so that we can use the code to like help
offload repeated tasks from the
animators like if you keep on having to
animate it like an overshoot for your
punch you could just add a new curve for
that we could add a little overshoot
curve and then for each of those
overshoots you just do the one keyframe
you apply that curve and just cut down
on your work a lot so if you keep on
identifying any repeated animation tasks
and extracting them out and making good
tools for the animators to use while
doing that I think that could save a lot
of time it would also allow animators to
focus a lot more on the performance and
less on the like busy work like
transition animations and repetitive
things and thanks for watching
it's on my contact info if you'd like to
talk afterwards
I rather than use your tools because
it's way cool it well I think it's a lot
easier to program yourself then it
Case Studies
probably seems like for the pose
matching I'll just make sure I'll just
read the relative matrices from the
animation and then I just apply a joint
constraint to enforce its relative
positions in bullet and things like that
well so I don't really have like
specific user-friendly tools that I can
sell but I could tell you how it works
thank you is that can you think of good
ways of like one of the main problems
with using just physics to move between
keyframes is you lose intentional
in-betweens which is something that
animators really like having control
over is can you think of a good way of
biasing the in-betweens towards certain
performance focus in betweens rather
than just submitting to what physics
dictates well it's so kind of animating
in the same way as you normally would
but you would take the transition that
you would normally animate with the
intentional in-betweens and then kind of
trying to abstract that out a little bit
so can apply to multiple transitions
which is mostly what I'm trying to do
trying to like avoid the combinatorial
explosion of trying to transition from
every stand to every crouch so we can
kind of make it more of a linear not
explosion it's possible yeah so you were
talking about you you coded in basic to
bone I K for your wall hanging for
instance how is there like a resource
that you used to to maybe learn more
about that or I mean could you point me
in the right direction maybe because I'm
not it's something I'm unfamiliar with
and I'd like to figure out as well I
can't think of a specific resource for
that but you could talk about it on
email oh sure it's basically just it's
like the right triangle problem like you
have just bone in this bone now you have
two right triangles so you find this
angle here okay yeah
they did so you mentioned the case of
like having the character to hold like a
ball or something like that
and you said that the approach for that
would be just to have you know make all
those key frames except with him holding
a ball
did you try doing like putting IK so
you put his hands onto the ball and then
so you wouldn't have to make those all
those frames again I'll try and answer
one minute yeah I think you can
definitely do a lot of that with IK I
try not to I try to only use IK when
it's like super needed because usually
I'll find better results by having it
like hold a very small ball and hold a
very large bar while and then
interpolate because then we have
animation control of the extremes or do
that with an animation layer that gets
layered on top because otherwise you're
animating with very awkward tools so
you're like animating in a text editor
which is not ideal okay well thanks I
don't think
[Applause]